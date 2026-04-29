using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Time;

namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Beregner Vakt-ROI per nedetids-event:
///
///   ekstra_timer_spart = max(0, counterfactual_end − faktisk_end)
///   reddet_mwh         = ekstra_timer_spart × installert_effekt_mw × kapasitetsfaktor
///   reddet_nok         = reddet_mwh × snitt_spotpris_for_perioden
///
/// "Faktisk_end" hentes fra event.EndUtc — dette er tiden FAKTISK, med vakt-respons
/// allerede iberegnet fordi vakt-tjenesten har gjort jobben.
/// "Counterfactual_end" beregnes fra <see cref="VaktTidsmodell.NesteArbeidsdagOppstart"/>:
/// neste arbeidsdag kl. 08:00 lokal tid.
///
/// Filter for "reddbare" events:
///   – TripFeil (FO + cause "operlog:fault"/"operlog:alarm"/"U1-UnplannedStop")
///   – EksternForstyrrelse (nett-fall etc., konservativt regnet med i v1)
///   – Andre kategorier (PlanlagtVedlikehold, Markedstopp, DataMangel, Ressursmangel)
///     regnes IKKE — vakten ville ikke akselerert dem.
///
/// Krever at eventet starter innenfor vakt-vinduet (15-07 hverdager + helg/helligdag).
/// Hvis eventet starter i ordinær arbeidstid (08-15 mandag-fredag) er det driftspersonell
/// som responderer, ikke vakten — ingen ROI.
/// </summary>
public sealed class VaktRoiCalculator
{
    private static readonly HashSet<DowntimeEventCategory> ReddbareKategorier = new()
    {
        DowntimeEventCategory.TripFeil,
        DowntimeEventCategory.EksternForstyrrelse,
        DowntimeEventCategory.UkjentNedetid, // konservativ default
    };

    private readonly VaktTidsmodell _vaktModell;

    public VaktRoiCalculator() : this(new VaktTidsmodell()) { }

    public VaktRoiCalculator(VaktTidsmodell vaktModell)
    {
        _vaktModell = vaktModell ?? throw new ArgumentNullException(nameof(vaktModell));
    }

    public VaktTidsmodell Model => _vaktModell;

    /// <summary>
    /// Kjører ROI-beregning for et sett events.
    /// </summary>
    /// <param name="events">Aggregerte downtime-events fra <see cref="INedetidQueryService"/>.</param>
    /// <param name="installertEffektMw">Plant-kapasitet i MW. Brukes til å estimere reddet produksjon.</param>
    /// <param name="snittSpotprisNokMwh">
    /// Gjennomsnittlig spotpris for perioden i NOK/MWh. Brukes til å verdsette
    /// "ekstra timer" som ville oppstått uten vakt — disse timene ligger per
    /// definisjon utenfor settlement-vinduet og har ikke kjent spotpris, så
    /// vi bruker periodens snitt som beste tilgjengelige estimat.
    /// </param>
    /// <param name="kapasitetsfaktor">
    /// Forventet utnyttelses-grad for de "ekstra timene". For norske
    /// elvekraftverk: typisk 0.4-0.6 over et år, men kan være lavere i tørre
    /// perioder. v1-default 0.5; bedre estimat kan beregnes per anlegg fra
    /// historikk i v2.
    /// </param>
    public IReadOnlyList<VaktRoiResultat> Calculate(
        IReadOnlyList<DowntimeEvent> events,
        double installertEffektMw,
        double snittSpotprisNokMwh,
        double kapasitetsfaktor = 0.5)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installertEffektMw);
        ArgumentOutOfRangeException.ThrowIfNegative(kapasitetsfaktor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(kapasitetsfaktor, 1.0);

        var result = new List<VaktRoiResultat>(events.Count);

        foreach (var e in events)
        {
            var innenforVakt = _vaktModell.ErInnenforVakt(e.StartUtc);
            var reddbar = ReddbareKategorier.Contains(e.Category);

            if (!innenforVakt)
            {
                result.Add(new VaktRoiResultat
                {
                    Event = e,
                    ErInnenforVakt = false,
                    ErReddbar = reddbar,
                    CounterfactualEndUtc = null,
                    EkstraTimerSpart = 0,
                    ReddetMwh = 0,
                    ReddetNok = 0,
                    Forklaring = "Event startet i ordinær arbeidstid — driftspersonell responderer, ikke vakten.",
                });
                continue;
            }

            if (!reddbar)
            {
                result.Add(new VaktRoiResultat
                {
                    Event = e,
                    ErInnenforVakt = true,
                    ErReddbar = false,
                    CounterfactualEndUtc = null,
                    EkstraTimerSpart = 0,
                    ReddetMwh = 0,
                    ReddetNok = 0,
                    Forklaring = $"Kategori '{e.Category}' regnes ikke som reddbar (planlagt/marked/data).",
                });
                continue;
            }

            // Counterfactual: neste arbeidsdag kl. 08:00 lokal tid etter eventets start.
            // Dette er når driftspersonell ville møtt opp uten vakt.
            var counterfactualEnd = _vaktModell.NesteArbeidsdagOppstart(e.StartUtc);

            // Ekstra timer = counterfactualEnd − faktisk_end. Hvis faktisk_end allerede er
            // forbi counterfactualEnd (lange events) → ingen ROI fra vakten på den delen.
            var ekstraTimer = (counterfactualEnd - e.EndUtc).TotalHours;
            if (ekstraTimer < 0) ekstraTimer = 0;

            var reddetMwh = ekstraTimer * installertEffektMw * kapasitetsfaktor;
            var reddetNok = reddetMwh * snittSpotprisNokMwh;

            string forklaring;
            if (ekstraTimer == 0)
            {
                forklaring = "Faktisk varighet ville uansett strukket forbi neste arbeidsdag — ingen ekstra ROI.";
            }
            else
            {
                forklaring = $"Vakt løste på {e.VarighetTimer:F1} t. Uten vakt: vent til neste 08:00 lokal = "
                    + $"{ekstraTimer:F1} t ekstra nedetid, ≈ {reddetMwh:F1} MWh × {snittSpotprisNokMwh:F0} NOK/MWh.";
            }

            result.Add(new VaktRoiResultat
            {
                Event = e,
                ErInnenforVakt = true,
                ErReddbar = true,
                CounterfactualEndUtc = counterfactualEnd,
                EkstraTimerSpart = ekstraTimer,
                ReddetMwh = reddetMwh,
                ReddetNok = reddetNok,
                Forklaring = forklaring,
            });
        }

        return result;
    }
}
