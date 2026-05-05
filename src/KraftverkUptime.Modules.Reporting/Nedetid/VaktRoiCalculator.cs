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
        DowntimeEventCategory.TettInntaksrist,    // vakten kan rense rist + restarte
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
    /// <param name="overrides">
    /// Manuelle overrides per leder-event-StartUtc. Verdier: "HaddeOverlop"
    /// (tving full produksjons-redding), "IkkeOverlop" (sett produksjons-
    /// komponent til 0). Events uten override (eller med "Auto") bruker
    /// SCADA-overflow-data som vanlig.
    /// </param>
    public IReadOnlyList<VaktRoiResultat> Calculate(
        IReadOnlyList<DowntimeEvent> events,
        double installertEffektMw,
        double snittSpotprisNokMwh,
        double kapasitetsfaktor = 0.5,
        IReadOnlySet<DateTimeOffset>? overflowHours = null,
        bool overflowDataAvailable = false,
        double snittUbalansetillegg_NokMwh = 0,
        IReadOnlyDictionary<DateTimeOffset, string>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(installertEffektMw);
        ArgumentOutOfRangeException.ThrowIfNegative(kapasitetsfaktor);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(kapasitetsfaktor, 1.0);
        ArgumentOutOfRangeException.ThrowIfNegative(snittUbalansetillegg_NokMwh);

        overflowHours ??= new HashSet<DateTimeOffset>();
        overrides ??= new Dictionary<DateTimeOffset, string>();

        // Pass 1: klassifiser hvert event (utenfor-vakt / ikke-reddbar / reddbar)
        // og lag en arbeidsliste med counterfactualEnd per reddbar event.
        var classified = new List<(DowntimeEvent Event, EventClassification Class, DateTimeOffset? CounterfactualEnd)>(events.Count);
        foreach (var e in events)
        {
            var innenforVakt = _vaktModell.ErInnenforVakt(e.StartUtc);
            var reddbar = ReddbareKategorier.Contains(e.Category);
            if (!innenforVakt)
            {
                classified.Add((e, EventClassification.UtenforVakt, null));
            }
            else if (!reddbar)
            {
                classified.Add((e, EventClassification.IkkeReddbar, null));
            }
            else
            {
                var cf = _vaktModell.NesteArbeidsdagOppstart(e.StartUtc);
                classified.Add((e, EventClassification.Reddbar, cf));
            }
        }

        // Pass 2: grupper reddbare events på (PlantId, counterfactualEnd).
        // Events i samme gruppe deler vakt-callout — drifts-leders 2026-05-04-
        // korreksjon: siden plantet er oppe igjen mellom events i samme helg,
        // skal ikke nye events i samme vindu øke ROI-omfanget.
        var groups = classified
            .Where(c => c.Class == EventClassification.Reddbar)
            .GroupBy(c => (c.Event.PlantId, c.CounterfactualEnd!.Value))
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Event.StartUtc).Select(c => c.Event).ToList());

        // Pass 3: for hver gruppe, beregn felles ROI én gang og avgjør
        // hvilket event er "leder" (først i tid).
        //
        // Ekstra timer beregnes kontinuerlig (brøk-timer beholdes) slik at
        // ubalanse-komponenten matcher den eksakte tiden plantet er oppe.
        // Overflow telles per hele klokketime (gulv-kvantisert) fordi
        // overflow-data leveres per-time fra SCADA. Dette er semantisk likt
        // som gammel single-event-kalkulator, men generaliserer til grupper.
        var groupRoi = new Dictionary<(string, DateTimeOffset), GroupRoi>(groups.Count);
        foreach (var (key, members) in groups)
        {
            var leaderStart = members[0].StartUtc;
            var counterfactualEnd = key.Item2;

            // Slå sammen overlappende/back-to-back events til disjoinkte intervaller
            // innenfor [leaderStart, counterfactualEnd). Trim hver event til vinduet.
            var rawIntervals = members
                .Select(ev =>
                {
                    var s = ev.StartUtc < leaderStart ? leaderStart : ev.StartUtc;
                    var e = ev.EndUtc > counterfactualEnd ? counterfactualEnd : ev.EndUtc;
                    return (Start: s, End: e);
                })
                .Where(iv => iv.End > iv.Start)
                .OrderBy(iv => iv.Start)
                .ToList();

            var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            foreach (var iv in rawIntervals)
            {
                if (merged.Count > 0 && iv.Start <= merged[^1].End)
                {
                    var last = merged[^1];
                    merged[^1] = (last.Start, iv.End > last.End ? iv.End : last.End);
                }
                else
                {
                    merged.Add(iv);
                }
            }

            var totalOutageHours = merged.Sum(iv => (iv.End - iv.Start).TotalHours);
            var windowHours = (counterfactualEnd - leaderStart).TotalHours;
            var savedHours = Math.Max(0, windowHours - totalOutageHours);

            // Overflow: gulv-kvantiserte timer i vinduet som IKKE er dekket av
            // noe outage-intervall. Match SCADA-time-aggregat-modellen.
            var outageHourSet = new HashSet<DateTimeOffset>();
            foreach (var iv in merged)
            {
                var hStart = FloorToHour(iv.Start);
                var hEnd = FloorToHour(iv.End);
                for (var h = hStart; h < hEnd; h = h.AddHours(1)) outageHourSet.Add(h);
            }
            var savedOverflowHours = 0;
            var leaderStartHour = FloorToHour(leaderStart);
            var counterfactualHour = FloorToHour(counterfactualEnd);
            for (var h = leaderStartHour; h < counterfactualHour; h = h.AddHours(1))
            {
                if (outageHourSet.Contains(h)) continue;
                if (overflowHours.Contains(h)) savedOverflowHours++;
            }

            // Manuell override per leder-event: drifts-leder kan tvinge full
            // overflow-kreditt eller null kreditt uavhengig av SCADA-data.
            // Lagres i core.vakt_event_overrides per (plant_id, event_start_utc).
            var overrideClassification = overrides.TryGetValue(leaderStart, out var oc) ? oc : null;
            var overflowOverridden = false;
            switch (overrideClassification)
            {
                case "HaddeOverlop":
                    // Tving full produksjons-redding: alle ekstra-timer regnes
                    // som overflow (counterfactual-vindu minus outage, gulv-kvantisert).
                    savedOverflowHours = 0;
                    for (var h = leaderStartHour; h < counterfactualHour; h = h.AddHours(1))
                    {
                        if (!outageHourSet.Contains(h)) savedOverflowHours++;
                    }
                    overflowOverridden = true;
                    break;
                case "IkkeOverlop":
                    savedOverflowHours = 0;
                    overflowOverridden = true;
                    break;
            }

            groupRoi[key] = new GroupRoi(
                Members: members,
                LeaderStart: leaderStart,
                CounterfactualEnd: counterfactualEnd,
                SavedHours: savedHours,
                SavedOverflowHours: savedOverflowHours,
                OverflowOverridden: overflowOverridden);
        }

        // Pass 4: bygg per-event resultater. Leader får full gruppe-ROI;
        // medlems-events får 0 ROI med forklaring som peker tilbake til leder.
        var result = new List<VaktRoiResultat>(events.Count);
        foreach (var (e, klass, cf) in classified)
        {
            if (klass == EventClassification.UtenforVakt)
            {
                result.Add(new VaktRoiResultat
                {
                    Event = e,
                    ErInnenforVakt = false,
                    ErReddbar = ReddbareKategorier.Contains(e.Category),
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

            if (klass == EventClassification.IkkeReddbar)
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

            // Reddbar — slå opp gruppe og bestem rolle (leder/medlem)
            var groupKey = (e.PlantId, cf!.Value);
            var group = groupRoi[groupKey];
            var isLeader = ReferenceEquals(group.Members[0], e);

            if (!isLeader)
            {
                // Medlem — alt ROI er allerede attribuert til leder.
                var leader = group.Members[0];
                result.Add(new VaktRoiResultat
                {
                    Event = e,
                    ErInnenforVakt = true,
                    ErReddbar = true,
                    CounterfactualEndUtc = cf,
                    EkstraTimerSpart = 0,
                    ReddetMwh = 0,
                    ReddetNok = 0,
                    ReddetProduksjon_NOK = 0,
                    ReddetUbalanse_NOK = 0,
                    OverflowTimerInCounterfactual = 0,
                    OverflowDataMissing = false,
                    Forklaring =
                        $"Samme vakt-callout som event kl. {leader.StartUtc.LocalDateTime:dd.MM HH:mm} — " +
                        "ROI er allerede regnet på leder-eventet (vakta var allerede ute, ekstra hendelser " +
                        "i samme vindu øker ikke omfanget).",
                });
                continue;
            }

            // Leder — får full gruppe-ROI.
            var ekstraTimer = group.SavedHours;
            var overflowTimer = group.SavedOverflowHours;

            var reddetMwh = overflowTimer * installertEffektMw * kapasitetsfaktor;
            var reddetProduksjonNok = reddetMwh * snittSpotprisNokMwh;

            var ubalanseMwh = ekstraTimer * installertEffektMw * kapasitetsfaktor;
            var reddetUbalanseNok = ubalanseMwh * snittUbalansetillegg_NokMwh;

            var reddetTotalNok = reddetProduksjonNok + reddetUbalanseNok;

            var forklaring = BuildForklaring(
                e, ekstraTimer, overflowTimer, overflowDataAvailable,
                reddetMwh, snittSpotprisNokMwh,
                ubalanseMwh, snittUbalansetillegg_NokMwh,
                reddetProduksjonNok, reddetUbalanseNok);

            if (group.Members.Count > 1)
            {
                forklaring +=
                    $" (Leder for {group.Members.Count} events i samme vakt-vindu — ROI samles her.)";
            }

            if (group.OverflowOverridden)
            {
                forklaring +=
                    " ⚠ Manuelt overstyrt av drifts-leder (override aktiv på denne hendelsen).";
            }

            result.Add(new VaktRoiResultat
            {
                Event = e,
                ErInnenforVakt = true,
                ErReddbar = true,
                CounterfactualEndUtc = cf,
                EkstraTimerSpart = ekstraTimer,
                ReddetMwh = reddetMwh,
                ReddetNok = reddetTotalNok,
                ReddetProduksjon_NOK = reddetProduksjonNok,
                ReddetUbalanse_NOK = reddetUbalanseNok,
                OverflowTimerInCounterfactual = overflowTimer,
                // Hvis override er aktiv, regnes ikke data som "missing" uansett.
                OverflowDataMissing = !group.OverflowOverridden && !overflowDataAvailable && ekstraTimer > 0,
                Forklaring = forklaring,
            });
        }

        return result;
    }

    private enum EventClassification { UtenforVakt, IkkeReddbar, Reddbar }

    private sealed record GroupRoi(
        List<DowntimeEvent> Members,
        DateTimeOffset LeaderStart,
        DateTimeOffset CounterfactualEnd,
        double SavedHours,
        int SavedOverflowHours,
        bool OverflowOverridden);

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
