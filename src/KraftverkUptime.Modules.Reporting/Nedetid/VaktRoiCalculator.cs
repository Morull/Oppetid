using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Time;

namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Beregner Vakt-ROI per nedetids-event:
///
///   ekstra_timer_spart = max(0, counterfactual_end − faktisk_end)
///   reddbare_timer     = antall timer i counterfactual som hadde overløp
///   reddet_mwh         = reddbare_timer × installert_effekt_mw × kapasitetsfaktor
///   reddet_nok         = reddet_mwh × snitt_spotpris_for_perioden
///
/// "Faktisk_end" hentes fra event.EndUtc — dette er tiden FAKTISK, med vakt-respons
/// allerede iberegnet fordi vakt-tjenesten har gjort jobben.
/// "Counterfactual_end" beregnes fra <see cref="VaktTidsmodell.NesteArbeidsdagOppstart"/>:
/// neste arbeidsdag kl. 08:00 lokal tid.
///
/// Overløps-justering (spec 2026-04-29): Vakt-ROI gjelder kun timer der det var
/// overløp i magasinet i counterfactual-perioden. Hvis det ikke var overløp,
/// ville vannet uansett vært trygt magasinert og kunne brukes senere — vakten
/// reddet ingen produksjon. Dette er konservativt og lett å forsvare for
/// drifts-leder. Hvis SCADA-data mangler antar vi ingen overløp (ROI = 0) og
/// flagger eventet med <see cref="VaktRoiResultat.OverflowDataMissing"/>.
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
    /// <param name="overflowHours">
    /// Settet av timer (UTC, time-presisjon) i counterfactual-perioden der
    /// overløp ble registrert i magasinet. Kun disse timene bidrar til ROI —
    /// se klasse-dokumentasjon. Pass tom HashSet hvis SCADA-data mangler eller
    /// plantet ikke har overløps-tag (kombiner med <paramref name="overflowDataAvailable"/>
    /// for korrekt forklaring).
    /// </param>
    /// <param name="overflowDataAvailable">
    /// True hvis SCADA-data dekker counterfactual-perioden. False markerer
    /// eventene med <see cref="VaktRoiResultat.OverflowDataMissing"/> og gir
    /// en eksplisitt forklaring. Settes typisk false når plantet ikke har
    /// OverflowFlow-tag, eller når data mangler.
    /// </param>
    /// <param name="snittUbalansetillegg_NokMwh">
    /// Snitt-tillegg i NOK/MWh som vakten redder ved å unngå ubalanse-gebyr.
    /// Beregnes typisk som max(0, avg(RkPris − Spotpris)) over perioden:
    /// hvis RK var dyrere enn spot, så betalte producent denne differansen
    /// for hver MWh under-leveranse. Default 0 = ingen ubalanse-komponent
    /// (gir samme oppførsel som v2). Spec: SPEC-VAKT-ROI-UBALANSE.md.
    /// </param>
    public IReadOnlyList<VaktRoiResultat> Calculate(
        IReadOnlyList<DowntimeEvent> events,
        double installertEffektMw,
        double snittSpotprisNokMwh,
        double kapasitetsfaktor = 0.5,
        IReadOnlySet<DateTimeOffset>? overflowHours = null,
        bool overflowDataAvailable = false,
        double snittUbalansetillegg_NokMwh = 0)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installertEffektMw);
        ArgumentOutOfRangeException.ThrowIfNegative(kapasitetsfaktor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(kapasitetsfaktor, 1.0);
        ArgumentOutOfRangeException.ThrowIfNegative(snittUbalansetillegg_NokMwh);

        overflowHours ??= new HashSet<DateTimeOffset>();
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
                    ReddetProduksjon_NOK = 0,
                    ReddetUbalanse_NOK = 0,
                    OverflowTimerInCounterfactual = 0,
                    OverflowDataMissing = false,
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
                    ReddetProduksjon_NOK = 0,
                    ReddetUbalanse_NOK = 0,
                    OverflowTimerInCounterfactual = 0,
                    OverflowDataMissing = false,
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

            // Tell antall timer i counterfactual-perioden som hadde overløp i magasinet.
            // Vakt-ROI gjelder kun for disse — uten overløp er vannet trygt magasinert.
            var overflowTimer = ekstraTimer > 0
                ? CountOverflowHours(e.EndUtc, counterfactualEnd, overflowHours)
                : 0;

            // ---- Produksjons-komponent (overflow-betinget, fra v2) -----------
            var reddetMwh = overflowTimer * installertEffektMw * kapasitetsfaktor;
            var reddetProduksjonNok = reddetMwh * snittSpotprisNokMwh;

            // ---- Ubalanse-komponent (alle counterfactual-timer, ny i v3) ----
            // Spotbud-forpliktelsen står uavhengig av magasinstand — vakten
            // redder ubalanse-gebyret i hele counterfactual-vinduet.
            var ubalanseMwh = ekstraTimer * installertEffektMw * kapasitetsfaktor;
            var reddetUbalanseNok = ubalanseMwh * snittUbalansetillegg_NokMwh;

            var reddetTotalNok = reddetProduksjonNok + reddetUbalanseNok;

            var forklaring = BuildForklaring(
                e, ekstraTimer, overflowTimer, overflowDataAvailable,
                reddetMwh, snittSpotprisNokMwh,
                ubalanseMwh, snittUbalansetillegg_NokMwh,
                reddetProduksjonNok, reddetUbalanseNok);

            result.Add(new VaktRoiResultat
            {
                Event = e,
                ErInnenforVakt = true,
                ErReddbar = true,
                CounterfactualEndUtc = counterfactualEnd,
                EkstraTimerSpart = ekstraTimer,
                ReddetMwh = reddetMwh,
                ReddetNok = reddetTotalNok,
                ReddetProduksjon_NOK = reddetProduksjonNok,
                ReddetUbalanse_NOK = reddetUbalanseNok,
                OverflowTimerInCounterfactual = overflowTimer,
                OverflowDataMissing = !overflowDataAvailable && ekstraTimer > 0,
                Forklaring = forklaring,
            });
        }

        return result;
    }

    /// <summary>
    /// Teller hele timer i [<paramref name="fromUtc"/>, <paramref name="toUtc"/>)
    /// som har en match i <paramref name="overflowHours"/>. Brøk-timer ved
    /// kantene rundes til nærmeste hele time-grense (start: gulv, slutt:
    /// gulv) — det matcher hvordan SCADA leverer time-aggregat (én rad per
    /// hel klokketime).
    /// </summary>
    private static int CountOverflowHours(
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        IReadOnlySet<DateTimeOffset> overflowHours)
    {
        if (overflowHours.Count == 0) return 0;
        if (toUtc <= fromUtc) return 0;

        var startHour = FloorToHour(fromUtc);
        var endHour = FloorToHour(toUtc);

        var count = 0;
        for (var h = startHour; h < endHour; h = h.AddHours(1))
        {
            if (overflowHours.Contains(h)) count++;
        }
        return count;
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Bygger forklaringsteksten ut fra hvilke ROI-komponenter som er ulik 0.
    /// Uten ubalanse-komponent matcher meldingen v2 nøyaktig (bakoverkompabilitet
    /// for tester og UI). Med ubalanse legges en ekstra setning til.
    /// </summary>
    private static string BuildForklaring(
        DowntimeEvent e,
        double ekstraTimer,
        int overflowTimer,
        bool overflowDataAvailable,
        double reddetMwh, double snittSpot,
        double ubalanseMwh, double snittUbalansetillegg,
        double reddetProduksjonNok, double reddetUbalanseNok)
    {
        if (ekstraTimer == 0)
        {
            return "Faktisk varighet ville uansett strukket forbi neste arbeidsdag — ingen ekstra ROI.";
        }

        // Ubalanse-komponenten avhenger ikke av overflow-data, så den kan vises
        // selv når overflow-data mangler.
        var hasUbalanse = snittUbalansetillegg > 0 && reddetUbalanseNok > 0;

        if (!overflowDataAvailable)
        {
            var msg = $"Vakt løste på {e.VarighetTimer:F1} t. SCADA mangler overløps-data for "
                + $"counterfactual-perioden ({ekstraTimer:F1} t) — kan ikke beregne ROI.";
            if (hasUbalanse)
            {
                msg += $" Ubalanse-gebyr unngått: ≈ {ubalanseMwh:F1} MWh × "
                    + $"{snittUbalansetillegg:F0} NOK/MWh = {reddetUbalanseNok:F0} NOK.";
            }
            return msg;
        }

        if (overflowTimer == 0)
        {
            var msg = $"Vakt løste på {e.VarighetTimer:F1} t. Counterfactual = {ekstraTimer:F1} t, "
                + "men ingen overløp i perioden — vannet ville vært magasinert.";
            if (hasUbalanse)
            {
                msg += $" Vakten reddet bare ubalanse-gebyret: ≈ {reddetUbalanseNok:F0} NOK "
                    + $"({ubalanseMwh:F1} MWh × {snittUbalansetillegg:F0} NOK/MWh).";
            }
            else
            {
                msg += " Ingen ROI.";
            }
            return msg;
        }

        // overflowTimer > 0
        var produksjonsDel = $"Vakt løste på {e.VarighetTimer:F1} t. Counterfactual = {ekstraTimer:F1} t. "
            + $"Av disse hadde {overflowTimer} t overløp i magasinet → {overflowTimer} t reddet "
            + $"(≈ {reddetMwh:F1} MWh × {snittSpot:F0} NOK/MWh = {reddetProduksjonNok:F0} NOK).";
        if (!hasUbalanse)
        {
            return produksjonsDel;
        }
        return produksjonsDel
            + $" Ubalanse-gebyr unngått for hele counterfactual: {reddetUbalanseNok:F0} NOK "
            + $"(≈ {ubalanseMwh:F1} MWh × {snittUbalansetillegg:F0} NOK/MWh). "
            + $"Total reddet: {reddetProduksjonNok + reddetUbalanseNok:F0} NOK.";
    }
}
