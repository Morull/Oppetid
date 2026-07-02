using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Time;

namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Beregner Vakt-ROI per nedetids-event:
///
///   ekstra_timer_spart = max(0, counterfactual_end − faktisk_end)
///   reddet_mwh         = sum(ProduksjonplanMwh for timer i counterfactual som
///                              hadde overløp OG ikke er dekket av outage)
///   ubalanse_mwh       = sum(ProduksjonplanMwh for timer i counterfactual som
///                              ikke er dekket av outage)
///   reddet_nok         = reddet_mwh × snitt_spotpris_for_perioden
///                      + sum(plan_time × ubalansepremie_time) over samme timer
///
/// Ubalanse-komponenten dekker HELE counterfactual-vinduet: Hydrogrid melder inn
/// produksjon automatisk for påfølgende døgn, så anlegget står Spotbud-forpliktet
/// i hele den ekstra nedetiden vakten avverget — det finnes ingen manuell
/// «nullstilling av bud» ved day-ahead gate closure. Hver time verdsettes på
/// timens faktiske (RK − spot) når per-time-premier er gitt; ellers brukes
/// periodesnittet. (SPEC-VAKT-ROI-UBALANSE-FULLPERIODE-OG-VISNING, 2026-06-17 —
/// avløser gate-closure-cappen fra SPEC-UBALANSE-ENPRIS-FIX.)
///
/// "Faktisk_end" hentes fra event.EndUtc — dette er tiden FAKTISK, med vakt-respons
/// allerede iberegnet fordi vakt-tjenesten har gjort jobben.
/// "Counterfactual_end" beregnes fra <see cref="VaktTidsmodell.NesteArbeidsdagOppstart"/>:
/// neste arbeidsdag kl. 08:00 lokal tid.
///
/// Plan-data (<c>ProduksjonplanMwh</c> fra Hydrogrid-plan) brukes som basis
/// istedenfor en flat <c>installertEffektMw × kapasitetsfaktor</c>. Plan
/// reflekterer faktisk vannmengde/markedssituasjon producer hadde forventet
/// å kjøre på, så det fanger sesongvariasjon automatisk. For events der
/// counterfactual-vinduet strekker seg forbi importert settlement-data,
/// brukes nærmeste samme-ukedag/time bakover i tid som proxy (maks 4 ukers
/// vindu); slike events flagges med <see cref="VaktRoiResultat.PlanDataPartial"/>.
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
    /// <param name="snittSpotprisNokMwh">
    /// Gjennomsnittlig spotpris for perioden i NOK/MWh. Brukes til å verdsette
    /// "ekstra timer" som ville oppstått uten vakt — disse timene ligger per
    /// definisjon utenfor settlement-vinduet og har ikke kjent spotpris, så
    /// vi bruker periodens snitt som beste tilgjengelige estimat.
    /// </param>
    /// <param name="planByHour">
    /// Plan-data: <c>ProduksjonplanMwh</c> per UTC-time-presisjon. Dekker
    /// både settlement-perioden og counterfactual-utvidelsen (typisk
    /// fromUtc til toUtc + 3 dager for å fange counterfactual-end). Verdier
    /// for timer der settlement-data manglet er allerede fylt inn av tjenesten
    /// via proxy-fallback (samme ukedag/time bakover). Bruk
    /// <paramref name="proxyHours"/> til å vite hvilke som er proxy-utfylt.
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
    /// SIGNERT forventet ubalanse-merkost i NOK/MWh: <c>avg(ubalansepris − spot)</c>
    /// over ALLE timer i perioden (énprismodell). Kan være negativ når ubalanse i
    /// snitt var billigere enn spot (typisk i NO2). Default 0 = ingen ubalanse-
    /// komponent. Brukes som fallback-premie for counterfactual-timer som mangler
    /// i <paramref name="ubalansePremieByHour"/> (eller for alle timer når den er
    /// null). Ubalansen dekker hele counterfactual-vinduet — se klassedoc.
    /// FAGVURDERING #1 / SPEC-UBALANSE-ENPRIS-FIX (avløser toprismodell-antakelsen
    /// i SPEC-VAKT-ROI-UBALANSE.md).
    /// </param>
    /// <param name="overrides">
    /// Manuelle overrides per leder-event-StartUtc. Verdier: "HaddeOverlop"
    /// (tving full produksjons-redding), "IkkeOverlop" (sett produksjons-
    /// komponent til 0). Events uten override (eller med "Auto") bruker
    /// SCADA-overflow-data som vanlig.
    /// </param>
    /// <param name="proxyHours">
    /// Settet av timer der <paramref name="planByHour"/>-verdien er proxy-utfylt
    /// (samme ukedag/time bakover i tid, opptil 4 uker). Events som har ≥ 1
    /// counterfactual-time i dette settet flagges med
    /// <see cref="VaktRoiResultat.PlanDataPartial"/>. Null = ingen proxy-info
    /// (alle hours regnes som direkte plan-data).
    /// </param>
    /// <param name="vaktOptions">
    /// Valgfri overstyring av vakt-vinduet for denne ene beregningen — brukes
    /// av endepunktet når drifts-leder simulerer alternative vakt-ordninger
    /// (eks. dropp av nattvakt 22-07). Hvis null brukes konstruktør-injected
    /// <c>_vaktModell</c>. Helg- og helligdags-håndteringen er IKKE konfigurerbar
    /// — hele lørdag/søndag/helligdag er fortsatt vakt-aktiv.
    /// </param>
    /// <param name="excludeFromReddbar">
    /// Settet av event-<c>StartUtc</c>-er som skal ekskluderes fra reddbar-
    /// klassifiseringen, uavhengig av kategori. Brukes av Vakt-ROI-endepunktene
    /// for å filtrere ut U2-PlanDeviation-hendelser uten operlog-match (spec
    /// NESTE-CHAT-VAKTROI-PLANDEVIATION-FILTER.md, 2026-05-22). Hendelser i
    /// settet blir markert som <c>IkkeReddbar</c> med null ROI, men teller
    /// fortsatt som outage-tid i counterfactual-vinduet (= bryter ikke
    /// monoton-invarianten). Selve EffectiveGuardResponse-logikken sitter
    /// utenfor kalkulatoren i <c>EffectiveGuardResponseEvaluator</c>.
    /// </param>
    /// <param name="damSamples">
    /// Time-pivoterte magasin-samples (volum/dam-flow/turbin-flow) som dekker
    /// minst 24 t før tidligste leder-event og fram til seneste counterfactual-
    /// slutt. Brukes til tilsig-basert estimat av counterfactual-overløp
    /// (SPEC-VAKT-ROI-OVERLOP-V2). Null = ingen estimat-kilde → kun observert
    /// overløp (dagens oppførsel).
    /// </param>
    /// <param name="maxVolumeM3">
    /// Maks magasin-volum (m³) for terminal-dammen. 0 = estimat ikke tilgjengelig.
    /// </param>
    /// <param name="fillRateByHour">
    /// Fyllgrad (ratio 0..1) per UTC-time. Kalkulatoren bruker siste verdi før
    /// hver leder-gruppes start som fyllgrad ved hendelsesstart. Null = ingen
    /// fyllgrad → estimatoren returnerer DataAvailable=false.
    /// </param>
    /// <param name="ubalansePremieByHour">
    /// SIGNERT ubalansepremie <c>(RkPris − Spotpris)</c> i NOK/MWh per UTC-time,
    /// analogt med <paramref name="planByHour"/>. Når satt verdsettes hver
    /// counterfactual-time på timens faktiske premie i stedet for periodesnittet —
    /// et periodesnitt er strukturelt negativt i NO2 og maskerer at enkelt-
    /// hendelser i knapphetstimer (RK ≫ spot) har sterkt positiv ubalanse-redning
    /// (SPEC-VAKT-ROI-UBALANSE-FULLPERIODE-OG-VISNING funn 1b, Variant 2). Timer
    /// som mangler i ordboken faller tilbake på
    /// <paramref name="snittUbalansetillegg_NokMwh"/>. Null = bruk periodesnittet
    /// for alle timer (bakoverkompatibelt).
    /// </param>
    public IReadOnlyList<VaktRoiResultat> Calculate(
        IReadOnlyList<DowntimeEvent> events,
        double snittSpotprisNokMwh,
        IReadOnlyDictionary<DateTimeOffset, double> planByHour,
        IReadOnlySet<DateTimeOffset>? overflowHours = null,
        bool overflowDataAvailable = false,
        double snittUbalansetillegg_NokMwh = 0,
        IReadOnlyDictionary<DateTimeOffset, string>? overrides = null,
        IReadOnlySet<DateTimeOffset>? proxyHours = null,
        VaktTidsmodellOptions? vaktOptions = null,
        IReadOnlySet<DateTimeOffset>? excludeFromReddbar = null,
        IReadOnlyList<InflowOverflowEstimator.HourlySample>? damSamples = null,
        double maxVolumeM3 = 0,
        IReadOnlyDictionary<DateTimeOffset, double>? fillRateByHour = null,
        IReadOnlyDictionary<DateTimeOffset, double>? ubalansePremieByHour = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(planByHour);
        // snittUbalansetillegg_NokMwh er en SIGNERT forventningsverdi (énpris) og
        // kan være negativ — ingen guard. FAGVURDERING #1 / SPEC-UBALANSE-ENPRIS-FIX.

        overflowHours ??= new HashSet<DateTimeOffset>();
        overrides ??= new Dictionary<DateTimeOffset, string>();
        proxyHours ??= new HashSet<DateTimeOffset>();
        excludeFromReddbar ??= new HashSet<DateTimeOffset>();

        // Hvis caller har gitt egne vakt-tider for denne spørringen, bygg en
        // lokal modell. Ellers bruk default fra konstruktør (DI-injected).
        var vaktModell = vaktOptions is null ? _vaktModell : new VaktTidsmodell(vaktOptions);

        // Pass 1: klassifiser hvert event (utenfor-vakt / ikke-reddbar / reddbar)
        // og lag en arbeidsliste med counterfactualEnd per reddbar event.
        // Events i excludeFromReddbar blir IkkeReddbar uansett kategori — det er
        // U2-PlanDeviation-filteret som anvendes oppstrøms av endepunktene.
        var classified = new List<(DowntimeEvent Event, EventClassification Class, DateTimeOffset? CounterfactualEnd)>(events.Count);
        foreach (var e in events)
        {
            var innenforVakt = vaktModell.ErInnenforVakt(e.StartUtc);
            var manueltEkskludert = excludeFromReddbar.Contains(e.StartUtc);
            var reddbar = !manueltEkskludert && ReddbareKategorier.Contains(e.Category);
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
                var cf = vaktModell.NesteArbeidsdagOppstart(e.StartUtc);
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

        // Bygg plant-spesifikt event-arkiv så outage-beregningen kan se ALLE
        // events i counterfactual-vinduet — ikke bare gruppe-medlemmer.
        // Spec NESTE-CHAT-VAKTROI-OG-UI-FIKS.md Del A: monoton-invarianten
        // brytes når et event er UtenforVakt (eller IkkeReddbar) men faktisk
        // skjedde inne i en leder-gruppes counterfactual-vindu. Hvis vi ikke
        // teller den outage-tiden, undervurderes totalOutageHours og
        // savedHours blir kunstig høyere → custom-vindu kan ende opp med å
        // "redde mer" enn et bredere vindu som inneholder samme tidsrom.
        var allEventsByPlant = events
            .GroupBy(e => e.PlantId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.StartUtc).ToList());

        // Pass 3: for hver gruppe, beregn felles ROI én gang og avgjør
        // hvilket event er "leder" (først i tid).
        //
        // Ekstra timer beregnes kontinuerlig (brøk-timer beholdes) slik at
        // BuildForklaring kan vise nøyaktig tid plantet ville stått.
        // Plan-summering går per hele klokketime (gulv-kvantisert) fordi
        // plan-data leveres per-time fra settlement.
        var groupRoi = new Dictionary<(string, DateTimeOffset), GroupRoi>(groups.Count);
        foreach (var (key, members) in groups)
        {
            var leaderStart = members[0].StartUtc;
            var counterfactualEnd = key.Item2;

            // Bygg outage-intervaller fra ALLE events for samme plant som
            // overlapper [leaderStart, counterfactualEnd) — ikke bare gruppe-
            // medlemmer. Hvis et IkkeReddbar/UtenforVakt-event faktisk skjedde
            // i vinduet, var anlegget nede de timene uansett, og vakta kunne
            // ikke ha reddet de.
            var allForPlant = allEventsByPlant.TryGetValue(key.Item1, out var allP)
                ? allP : new List<DowntimeEvent>();
            var rawIntervals = allForPlant
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

            // Bygg gulv-kvantisert outage-set så vi vet hvilke hele klokketimer
            // som er dekket av nedetid (= ikke skal telles som "reddet").
            var outageHourSet = new HashSet<DateTimeOffset>();
            foreach (var iv in merged)
            {
                var hStart = FloorToHour(iv.Start);
                var hEnd = FloorToHour(iv.End);
                for (var h = hStart; h < hEnd; h = h.AddHours(1)) outageHourSet.Add(h);
            }

            // Pass over hele counterfactual-vinduet: summer plan per time
            // for overflow-redning og ubalanse-redning separat.
            var leaderStartHour = FloorToHour(leaderStart);
            var counterfactualHour = FloorToHour(counterfactualEnd);

            // Tilsig-basert estimat av counterfactual-overløp (SPEC-VAKT-ROI-
            // OVERLOP-V2). Observert SCADA-overløp er bare en NEDRE grense: i
            // counterfactual-scenariet står turbinen i hele vinduet og magasinet
            // fylles. Estimatoren sier når magasinet ville vært fullt; fra det
            // punktet krediteres produksjon i TILLEGG til observerte overløpstimer.
            // Kjøres kun når dam-telemetri (samples + maks-volum + fyllgrad) finnes;
            // ellers DataAvailable=false og oppførselen er uendret (kun observert).
            var estimate = EstimerCounterfactualOverlop(
                damSamples, maxVolumeM3, fillRateByHour, leaderStart, counterfactualEnd);
            var fullAtUtc = (estimate?.DataAvailable == true
                    && !double.IsPositiveInfinity(estimate.HoursToFull))
                ? leaderStart.AddHours(estimate.HoursToFull)
                : (DateTimeOffset?)null;

            double reddetMwh = 0;
            double ubalanseMwh = 0;
            double ubalanseNok = 0;
            var savedOverflowObserved = 0;
            var savedOverflowEstimated = 0;
            var anyProxyHour = false;
            for (var h = leaderStartHour; h < counterfactualHour; h = h.AddHours(1))
            {
                if (outageHourSet.Contains(h)) continue;
                var planForHour = planByHour.TryGetValue(h, out var pv) ? pv : 0.0;
                if (planForHour < 0) planForHour = 0; // beskytt mot rare verdier
                if (proxyHours.Contains(h)) anyProxyHour = true;

                // Ubalanse-komponenten gjelder HELE counterfactual-vinduet. Hydrogrid
                // melder inn produksjon automatisk for påfølgende døgn, så forpliktelsen
                // til å levere innmeldt produksjon består i hele den ekstra nedetiden
                // vakten avverget — det finnes ingen manuell «nullstilling av bud» ved
                // gate closure. Hver time verdsettes på timens faktiske (RK − spot)
                // når per-time-premier er gitt; ellers på periodesnittet.
                // (Endring 2026-06-17, avløser gate-closure-cappen.)
                var premie = ubalansePremieByHour is not null
                    ? (ubalansePremieByHour.TryGetValue(h, out var ph) ? ph : snittUbalansetillegg_NokMwh)
                    : snittUbalansetillegg_NokMwh;
                ubalanseMwh += planForHour;                 // beholdes for forklaring/visning
                ubalanseNok += planForHour * premie;        // faktisk NOK per time

                // Produksjons-komponenten: observert overløp (nedre grense) ELLER
                // estimert fullt magasin fra og med fullAtUtc. else-if-en sikrer at
                // en time som er BÅDE observert og estimert kun telles én gang.
                if (overflowHours.Contains(h))
                {
                    reddetMwh += planForHour;
                    savedOverflowObserved++;
                }
                else if (fullAtUtc.HasValue && h >= FloorToHour(fullAtUtc.Value))
                {
                    reddetMwh += planForHour;
                    savedOverflowEstimated++;
                }
            }

            // Manuell override per leder-event: drifts-leder kan tvinge full
            // overflow-kreditt eller null kreditt uavhengig av SCADA/estimat.
            // Lagres i core.vakt_event_overrides per (plant_id, event_start_utc).
            // Override TRUMFER estimatet.
            var overrideClassification = overrides.TryGetValue(leaderStart, out var oc) ? oc : null;
            var overflowOverridden = false;
            switch (overrideClassification)
            {
                case "HaddeOverlop":
                    // Tving full produksjons-redding: alle ekstra-timer regnes
                    // som overflow (counterfactual-vindu minus outage, gulv-kvantisert).
                    reddetMwh = 0;
                    savedOverflowObserved = 0;
                    savedOverflowEstimated = 0;
                    for (var h = leaderStartHour; h < counterfactualHour; h = h.AddHours(1))
                    {
                        if (outageHourSet.Contains(h)) continue;
                        var planForHour = planByHour.TryGetValue(h, out var pv) ? pv : 0.0;
                        if (planForHour < 0) planForHour = 0;
                        reddetMwh += planForHour;
                        savedOverflowObserved++;
                    }
                    overflowOverridden = true;
                    break;
                case "IkkeOverlop":
                    reddetMwh = 0;
                    savedOverflowObserved = 0;
                    savedOverflowEstimated = 0;
                    overflowOverridden = true;
                    break;
            }

            groupRoi[key] = new GroupRoi(
                Members: members,
                LeaderStart: leaderStart,
                CounterfactualEnd: counterfactualEnd,
                SavedHours: savedHours,
                SavedOverflowHoursObserved: savedOverflowObserved,
                SavedOverflowHoursEstimated: savedOverflowEstimated,
                ReddetMwh: reddetMwh,
                UbalanseMwh: ubalanseMwh,
                UbalanseNok: ubalanseNok,
                OverflowOverridden: overflowOverridden,
                OverflowEstimateAvailable: estimate?.DataAvailable == true,
                OverflowEstimateHoursToFull: (estimate?.DataAvailable == true
                        && !double.IsPositiveInfinity(estimate.HoursToFull))
                    ? estimate.HoursToFull : (double?)null,
                OverflowEstimateForklaring: estimate?.DataAvailable == true ? estimate.Forklaring : null,
                PlanDataPartial: anyProxyHour);
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
                    PlanDataPartial = false,
                    Forklaring = "Event startet i ordinær arbeidstid — driftspersonell responderer, ikke vakten.",
                });
                continue;
            }

            if (klass == EventClassification.IkkeReddbar)
            {
                // To grunner kan gi IkkeReddbar: (1) kategori er ikke i ReddbareKategorier,
                // eller (2) hendelsen er manuelt ekskludert oppstrøms (U2-PlanDeviation-
                // filter). Forklaringen skal speile riktig årsak så drifts-leder forstår
                // hvorfor ROI = 0 her.
                var ikkeReddbarForklaring = excludeFromReddbar.Contains(e.StartUtc)
                    ? "U2-PlanDeviation uten operlog-match — vakta rykket ikke ut (auto-vurdering). " +
                      "Drifts-leder kan overstyre i Detaljer-popup."
                    : $"Kategori '{e.Category}' regnes ikke som reddbar (planlagt/marked/data).";
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
                    PlanDataPartial = false,
                    Forklaring = ikkeReddbarForklaring,
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
                    PlanDataPartial = false,
                    Forklaring =
                        $"Samme vakt-callout som event kl. {leader.StartUtc.LocalDateTime:dd.MM HH:mm} — " +
                        "ROI er allerede regnet på leder-eventet (vakta var allerede ute, ekstra hendelser " +
                        "i samme vindu øker ikke omfanget).",
                });
                continue;
            }

            // Leder — får full gruppe-ROI.
            var ekstraTimer = group.SavedHours;
            var overflowTimer = group.SavedOverflowHoursObserved + group.SavedOverflowHoursEstimated;

            var reddetMwh = group.ReddetMwh;
            var reddetProduksjonNok = reddetMwh * snittSpotprisNokMwh;

            var ubalanseMwh = group.UbalanseMwh;
            var reddetUbalanseNok = group.UbalanseNok;

            var reddetTotalNok = reddetProduksjonNok + reddetUbalanseNok;

            // Effektiv premie for AKKURAT denne hendelsens timer (kan avvike fra
            // periodesnittet når per-time-premier brukes) — vises i forklaringen
            // så drifts-leder ser om hendelsen traff dyre eller billige timer.
            var effektivPremie = ubalanseMwh > 1e-9
                ? reddetUbalanseNok / ubalanseMwh
                : snittUbalansetillegg_NokMwh;

            var forklaring = BuildForklaring(
                e, ekstraTimer, overflowTimer, overflowDataAvailable,
                reddetMwh, snittSpotprisNokMwh,
                ubalanseMwh, effektivPremie,
                reddetProduksjonNok, reddetUbalanseNok,
                group.SavedOverflowHoursObserved, group.SavedOverflowHoursEstimated,
                group.OverflowEstimateAvailable, group.OverflowEstimateForklaring);

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

            if (group.PlanDataPartial)
            {
                forklaring +=
                    " ℹ Plan-data manglet for én eller flere counterfactual-timer; brukte proxy fra samme ukedag/time bakover.";
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
                SavedOverflowHoursObserved = group.SavedOverflowHoursObserved,
                SavedOverflowHoursEstimated = group.SavedOverflowHoursEstimated,
                OverflowEstimateAvailable = group.OverflowEstimateAvailable,
                OverflowEstimateHoursToFull = group.OverflowEstimateHoursToFull,
                OverflowEstimateForklaring = group.OverflowEstimateForklaring,
                // Hvis override er aktiv, regnes ikke data som "missing" uansett.
                OverflowDataMissing = !group.OverflowOverridden && !overflowDataAvailable && ekstraTimer > 0,
                PlanDataPartial = group.PlanDataPartial,
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
        int SavedOverflowHoursObserved,
        int SavedOverflowHoursEstimated,
        double ReddetMwh,
        double UbalanseMwh,
        double UbalanseNok,
        bool OverflowOverridden,
        bool OverflowEstimateAvailable,
        double? OverflowEstimateHoursToFull,
        string? OverflowEstimateForklaring,
        bool PlanDataPartial);

    private const int EstimatLookbackHours = 24;

    /// <summary>
    /// Kjører <see cref="InflowOverflowEstimator"/> for én leder-gruppe: slicer
    /// 24 t lookback før <paramref name="leaderStart"/> og bruker siste fyllgrad
    /// før start. Returnerer null hvis dam-telemetri mangler (kalleren har ikke
    /// sendt samples / maks-volum) — da krediteres kun observert overløp.
    /// </summary>
    private static InflowOverflowEstimator.EstimateResult? EstimerCounterfactualOverlop(
        IReadOnlyList<InflowOverflowEstimator.HourlySample>? damSamples,
        double maxVolumeM3,
        IReadOnlyDictionary<DateTimeOffset, double>? fillRateByHour,
        DateTimeOffset leaderStart,
        DateTimeOffset counterfactualEnd)
    {
        if (damSamples is null || maxVolumeM3 <= 0)
        {
            return null;
        }

        var lookbackStart = leaderStart.AddHours(-EstimatLookbackHours);
        var lookback = damSamples
            .Where(s => s.HourUtc >= lookbackStart && s.HourUtc < leaderStart)
            .ToList();

        double? fillAtStart = null;
        if (fillRateByHour is not null)
        {
            fillAtStart = fillRateByHour
                .Where(kv => kv.Key < leaderStart)
                .OrderByDescending(kv => kv.Key)
                .Select(kv => (double?)kv.Value)
                .FirstOrDefault();
        }

        return InflowOverflowEstimator.Estimate(
            lookback, leaderStart, counterfactualEnd, maxVolumeM3, fillAtStart);
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Bygger forklaringsteksten ut fra hvilke ROI-komponenter som er ulik 0.
    /// Bruker plan-baserte MWh-tall som basis. <paramref name="effektivPremie"/>
    /// er snittpremien for AKKURAT denne hendelsens counterfactual-timer
    /// (reddetUbalanseNok / ubalanseMwh), ikke nødvendigvis periodesnittet.
    /// </summary>
    private static string BuildForklaring(
        DowntimeEvent e,
        double ekstraTimer,
        int overflowTimer,
        bool overflowDataAvailable,
        double reddetMwh, double snittSpot,
        double ubalanseMwh, double effektivPremie,
        double reddetProduksjonNok, double reddetUbalanseNok,
        int savedOverflowObserved, int savedOverflowEstimated,
        bool overflowEstimateAvailable, string? estimateForklaring)
    {
        if (ekstraTimer == 0)
        {
            return "Faktisk varighet ville uansett strukket forbi neste arbeidsdag — ingen ekstra ROI.";
        }

        // Ubalanse-komponenten avhenger ikke av overflow-data, så den kan vises
        // selv når overflow-data mangler. Premien er signert, så komponenten kan
        // være negativ (ubalanse var i snitt billigere enn spot) — den skal da
        // OGSÅ vises, ikke skjules (SPEC-VAKT-ROI-UBALANSE-FULLPERIODE B3).
        var hasUbalanse = Math.Abs(effektivPremie) > 0.01;

        // Fortegnsnøytral ubalanse-setning: positivt beløp = gebyr unngått,
        // negativt = ubalanse var billigere enn spot (vakten reddet ingen
        // ubalanse-kostnad — den ga avkall på en gevinst).
        string UbalanseSetning() => reddetUbalanseNok >= 0
            ? $"Ubalanse-gebyr unngått: ≈ {ubalanseMwh:F1} MWh × "
                + $"{effektivPremie:F0} NOK/MWh = {reddetUbalanseNok:F0} NOK."
            : $"Ubalanse: ≈ {ubalanseMwh:F1} MWh × {effektivPremie:F0} NOK/MWh = "
                + $"{reddetUbalanseNok:F0} NOK (negativ → ubalanse var i snitt billigere "
                + "enn spot; vakten reddet ikke ubalanse-kostnad i denne perioden).";

        if (!overflowDataAvailable)
        {
            var msg = $"Vakt løste på {e.VarighetTimer:F1} t. SCADA mangler overløps-data for "
                + $"counterfactual-perioden ({ekstraTimer:F1} t) — kan ikke beregne ROI.";
            if (hasUbalanse)
            {
                msg += " " + UbalanseSetning();
            }
            return msg;
        }

        if (overflowTimer == 0)
        {
            var msg = $"Vakt løste på {e.VarighetTimer:F1} t. Counterfactual = {ekstraTimer:F1} t, "
                + "men ingen overløp i perioden — vannet ville vært magasinert.";
            // Estimatoren kjørte, men sa at magasinet ikke ville fylles i vinduet.
            if (overflowEstimateAvailable && estimateForklaring is not null)
            {
                msg += $" Tilsigsmodell: {estimateForklaring}";
            }
            if (hasUbalanse)
            {
                msg += " " + UbalanseSetning();
            }
            else
            {
                msg += " Ingen ROI.";
            }
            return msg;
        }

        // overflowTimer > 0 — skill observert (SCADA) fra estimert (tilsigsmodell).
        // Estimerte timer merkes «~» for å signalisere at de er modell-anslag.
        string overlopBeskrivelse;
        if (savedOverflowEstimated > 0 && savedOverflowObserved > 0)
        {
            overlopBeskrivelse = $"{savedOverflowObserved} t observert + ~{savedOverflowEstimated} t estimert (tilsigsmodell)";
        }
        else if (savedOverflowEstimated > 0)
        {
            overlopBeskrivelse = $"~{savedOverflowEstimated} t estimert overløp (tilsigsmodell)";
        }
        else
        {
            overlopBeskrivelse = $"{overflowTimer} t overløp i magasinet";
        }

        var produksjonsDel = $"Vakt løste på {e.VarighetTimer:F1} t. Counterfactual = {ekstraTimer:F1} t. "
            + $"Av disse: {overlopBeskrivelse} → "
            + $"{reddetMwh:F1} MWh fra plan × {snittSpot:F0} NOK/MWh = {reddetProduksjonNok:F0} NOK.";
        if (savedOverflowEstimated > 0 && estimateForklaring is not null)
        {
            produksjonsDel += $" [{estimateForklaring}]";
        }
        if (!hasUbalanse)
        {
            return produksjonsDel;
        }
        return produksjonsDel
            + " " + UbalanseSetning()
            + $" Total reddet: {reddetProduksjonNok + reddetUbalanseNok:F0} NOK.";
    }
}
