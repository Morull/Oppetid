using KraftverkUptime.Core.Time;

namespace KraftverkUptime.Modules.Reporting.CaptureRate;

/// <summary>
/// Pure-funksjon kalkulator for capture rate (begge varianter).
///
/// Spec NESTE-CHAT-CR-MERVERDI-OPPRYDDING.md (2026-05-22): rotet sammen tre
/// ulike mål kalt "merverdi". Skiller dem nå i fire klart adskilte mål:
///
///   <b>Capture rate</b> (rent timing-mål):
///     capture_price  = Σ(mwh_t × spot_t) / Σ(mwh_t)   — volumvektet spotpris
///     times_baseline = Σ(spot_t) / |H_all|            (tidsvektet snitt over alle timer)
///     times_cr       = capture_price / times_baseline
///   ⇒ CR &gt; 1 ⟺ timingen var bedre enn jevn fordeling.
///
///   <b>Timing-merverdi</b> (krone-tvillingen til CR):
///     timing_merverdi = Σ(mwh_t × spot_t) − Σ(mwh_t) × times_baseline
///   ⇒ Invariant: timing_merverdi &gt; 0 ⟺ CR &gt; 1 (alltid).
///
///   <b>Realisert vs spot</b> (utførelses-gapet — fikk vi faktisk spot for det
///   vi leverte?):
///     realisert_pris    = Σ(nok_t) / Σ(mwh_t)
///     realisert_vs_spot = Σ(nok_t − spot_t × mwh_t)
///   ⇒ Negativ verdi = vi fikk mindre enn spotverdien av leveransen
///     (typisk i høyvanns-måneder der overproduksjon ikke ble solgt på day-ahead).
///
///   <b>Dag-CR</b> (Excel-replika med 5/95-persentilfilter):
///     Per dag: oppnådd_d = Σ(mwh_h × spot_h)_dag / Σ(mwh_h)_dag (samme grunnlag som times-CR)
///              rå_d      = oppnådd_d / spot_d_snitt
///     Filter:  behold dager der rå_d ∈ [P5, P95] beregnet over historisk tidsserie
///     dag_cr  = Σ(mwh_d × rå_d) / Σ(mwh_d)      for filtrerte dager
///
/// Pure: ingen DB- eller IO-tilgang. Caller leverer ferdig-pivoterte timesrader
/// (settlement) og full historisk dag-serie for persentil-beregning.
/// </summary>
public static class CaptureRateCalculator
{
    /// <summary>Én time fra settlement-fila — alle felter nullable for å håndtere hull.</summary>
    public sealed record HourlyInput(
        DateTimeOffset TimeUtc,
        double? MwhElhub,
        double? SpotprisNokMwh,
        double? SpotomsetningNok);

    /// <summary>
    /// Aggregert dag for persentil-beregning — kommer typisk fra hele tidsserien
    /// (8+ år) for å matche Excel-modellens P5/P95-vinduer.
    ///
    /// <see cref="ElhubSpotValueDay"/> = Σ(MWh × spot) for dagen. Det er teller
    /// for det nye CR-grunnlaget (volumvektet spotpris i stedet for faktisk
    /// omsetning). <see cref="NokDay"/> beholdes for "Realisert vs spot"-
    /// beregningen.
    /// </summary>
    public sealed record DailyInput(
        DateOnly Date,
        double MwhDay,
        double NokDay,
        double SpotDayAvg,
        double ElhubSpotValueDay);

    public sealed record CaptureRateResult(
        double CapturePriceNokMwh,
        double TimesCr,
        double TimesBaselineNokMwh,
        double DagCr,
        double DagBaselineNokMwh,
        double TimingMerverdiNok,
        double RealisertPrisNokMwh,
        double RealisertVsSpotNok,
        int AntallTimer,
        int AntallTimerProduksjon,
        int AntallDager,
        int AntallDagerEtterFilter)
    {
        /// <summary>
        /// Bakover-kompatibelt alias — speiler det nye <see cref="TimingMerverdiNok"/>.
        /// Felt-navnet kan brukes som "den merverdien som er konsistent med CR".
        /// </summary>
        public double MerverdiNok => TimingMerverdiNok;
    }

    /// <summary>
    /// Beregner capture rate for en periode. <paramref name="historicalDailyForPercentile"/>
    /// brukes til 5/95-percentile-filtrering av rå_d og bør være hele anleggets
    /// historiske tidsserie (Excel-modellen beregner persentilen over alle år);
    /// hvis bare den valgte perioden sendes inn blir filteret konservativt mer
    /// inkluderende.
    /// </summary>
    public static CaptureRateResult Compute(
        IReadOnlyList<HourlyInput> hours,
        IReadOnlyList<DailyInput> historicalDailyForPercentile,
        double percentileLow = 0.05,
        double percentileHigh = 0.95)
    {
        ArgumentNullException.ThrowIfNull(hours);
        ArgumentNullException.ThrowIfNull(historicalDailyForPercentile);
        if (percentileLow < 0 || percentileLow >= percentileHigh || percentileHigh > 1)
        {
            throw new ArgumentException("PercentileLow må være i [0, 1) og lavere enn PercentileHigh.");
        }

        if (hours.Count == 0)
        {
            return Empty();
        }

        // ---- Felles aggregater ----
        // Telleren for CR er nå Σ(mwh × spot) — VOLUMVEKTET SPOTPRIS, ikke
        // faktisk omsetning. Faktisk omsetning (sumNok) brukes kun for "Realisert
        // vs spot"-utførelses-gapet. Slik kan CR > 1 ⟺ Timing-merverdi > 0
        // som invariant; tidligere kunne CR < 1 selv om timingen var positiv,
        // dersom faktisk omsetning per MWh var lav (høyvanns-måneder).
        double sumMwh = 0;
        double sumNok = 0;             // faktisk omsetning — for Realisert vs spot
        double sumElhubSpotValue = 0;  // Σ(mwh × spot) — for CR og Timing-merverdi
        double sumSpot = 0;
        var timerProduksjon = 0;
        var timerMedSpot = 0;
        double realisertVsSpot = 0;

        foreach (var h in hours)
        {
            if (h.SpotprisNokMwh.HasValue)
            {
                sumSpot += h.SpotprisNokMwh.Value;
                timerMedSpot++;
            }
            if (h.MwhElhub is > 0 && h.SpotprisNokMwh.HasValue && h.SpotomsetningNok.HasValue)
            {
                sumMwh += h.MwhElhub.Value;
                sumNok += h.SpotomsetningNok.Value;
                sumElhubSpotValue += h.MwhElhub.Value * h.SpotprisNokMwh.Value;
                realisertVsSpot += h.SpotomsetningNok.Value - h.SpotprisNokMwh.Value * h.MwhElhub.Value;
                timerProduksjon++;
            }
        }

        // Capture-pris = volumvektet spotpris (nytt grunnlag). Var tidligere
        // sumNok/sumMwh som blandet timing og markedsutførelse.
        var capturePrice = sumMwh > 0 ? sumElhubSpotValue / sumMwh : 0;
        var timesBaseline = timerMedSpot > 0 ? sumSpot / timerMedSpot : 0;
        var timesCr = timesBaseline > 0 ? capturePrice / timesBaseline : 0;

        // Timing-merverdi = krone-tvillingen til CR. Algebraisk identitet:
        // timing_merverdi = sumMwh × snittspot × (CR − 1) → samme fortegn som CR−1.
        var timingMerverdi = sumElhubSpotValue - sumMwh * timesBaseline;

        // Realisert vs spot = utførelses-gap. Realisert pris = faktisk omsetning
        // per MWh — kan være mindre enn spot i høyvanns-måneder.
        var realisertPris = sumMwh > 0 ? sumNok / sumMwh : 0;

        // ---- Dag-CR med persentilfilter ----
        var (dagCr, dagBaseline, antallDager, antallEtterFilter) = ComputeDagCr(
            hours, historicalDailyForPercentile, percentileLow, percentileHigh);

        return new CaptureRateResult(
            CapturePriceNokMwh: capturePrice,
            TimesCr: timesCr,
            TimesBaselineNokMwh: timesBaseline,
            DagCr: dagCr,
            DagBaselineNokMwh: dagBaseline,
            TimingMerverdiNok: timingMerverdi,
            RealisertPrisNokMwh: realisertPris,
            RealisertVsSpotNok: realisertVsSpot,
            AntallTimer: hours.Count,
            AntallTimerProduksjon: timerProduksjon,
            AntallDager: antallDager,
            AntallDagerEtterFilter: antallEtterFilter);
    }

    /// <summary>
    /// Aggregerer timer til dager (lokal Europe/Oslo) og beregner dag-CR.
    /// Persentilgrenser fra <paramref name="historical"/> (full tidsserie).
    /// </summary>
    private static (double DagCr, double Baseline, int AntallDager, int AntallEtterFilter)
        ComputeDagCr(
            IReadOnlyList<HourlyInput> hours,
            IReadOnlyList<DailyInput> historical,
            double pLow, double pHigh)
    {
        // Aggreger timene fra valgt periode til lokale dager
        var dailyInPeriod = AggregateDaily(hours);
        if (dailyInPeriod.Count == 0)
        {
            return (0, 0, 0, 0);
        }

        // Beregn rå_d for valgt periode (kun dager med produksjon + spot > 0).
        // NB: dag-oppnådd-prisen bruker nå Σ(MWh × spot) / Σ(MWh), samme grunnlag
        // som times-CR. Tidligere brukte den NokDay (faktisk omsetning), som ga
        // ulik teller for times-CR og dag-CR — det er nettopp det vi rydder vekk.
        var rawInPeriod = new List<(DateOnly Date, double Mwh, double Raw)>();
        foreach (var d in dailyInPeriod)
        {
            if (d.MwhDay <= 0 || d.ElhubSpotValueDay <= 0 || d.SpotDayAvg <= 0) continue;
            var oppnaadd = d.ElhubSpotValueDay / d.MwhDay;
            var raw = oppnaadd / d.SpotDayAvg;
            rawInPeriod.Add((d.Date, d.MwhDay, raw));
        }

        if (rawInPeriod.Count == 0)
        {
            return (0, 0, dailyInPeriod.Count, 0);
        }

        // Persentilgrenser beregnes over hele tilgjengelig historikk for å matche
        // Excel-modellens "alle år"-fenster. Hvis historikken er tom, fall tilbake
        // til persentil over kun valgt periode (mindre konservativt).
        var historicalRaws = historical
            .Where(d => d.MwhDay > 0 && d.ElhubSpotValueDay > 0 && d.SpotDayAvg > 0)
            .Select(d => (d.ElhubSpotValueDay / d.MwhDay) / d.SpotDayAvg)
            .ToList();
        if (historicalRaws.Count == 0)
        {
            historicalRaws = rawInPeriod.Select(x => x.Raw).ToList();
        }

        var (low, high) = ComputePercentileBounds(historicalRaws, pLow, pHigh);

        var filtered = rawInPeriod.Where(r => r.Raw >= low && r.Raw <= high).ToList();
        if (filtered.Count == 0)
        {
            return (0, 0, dailyInPeriod.Count, 0);
        }

        var sumMwh = filtered.Sum(r => r.Mwh);
        var weightedSum = filtered.Sum(r => r.Mwh * r.Raw);
        var dagCr = sumMwh > 0 ? weightedSum / sumMwh : 0;

        // Baseline: volumvektet snitt-spotpris i de filtrerte dagene
        var baselineMap = dailyInPeriod.ToDictionary(d => d.Date, d => d.SpotDayAvg);
        var weightedSpot = filtered.Sum(r => r.Mwh * baselineMap[r.Date]);
        var baseline = sumMwh > 0 ? weightedSpot / sumMwh : 0;

        return (dagCr, baseline, dailyInPeriod.Count, filtered.Count);
    }

    /// <summary>
    /// Aggregerer timer til lokale dager (Europe/Oslo). Time-stempel som ligger
    /// på samme lokal-dato havner i samme bucket. SpotDayAvg er aritmetisk snitt
    /// over alle 24 timer i døgnet uavhengig av produksjon.
    /// </summary>
    private static IReadOnlyList<DailyInput> AggregateDaily(IReadOnlyList<HourlyInput> hours)
    {
        var tz = TimeZones.Norway;
        var byDate = new Dictionary<DateOnly,
            (double Mwh, double Nok, double SpotSum, int SpotCount, double ElhubSpotValue)>();
        foreach (var h in hours)
        {
            var local = TimeZoneInfo.ConvertTime(h.TimeUtc, tz);
            var date = DateOnly.FromDateTime(local.DateTime);
            byDate.TryGetValue(date, out var cur);

            if (h.MwhElhub is > 0)
            {
                cur.Mwh += h.MwhElhub.Value;
                if (h.SpotprisNokMwh.HasValue)
                {
                    // Σ(MWh × spot) for dagen — telleren i den nye dag-CR-en.
                    cur.ElhubSpotValue += h.MwhElhub.Value * h.SpotprisNokMwh.Value;
                }
            }
            if (h.SpotomsetningNok is > 0 && h.MwhElhub is > 0) cur.Nok += h.SpotomsetningNok.Value;
            if (h.SpotprisNokMwh.HasValue)
            {
                cur.SpotSum += h.SpotprisNokMwh.Value;
                cur.SpotCount++;
            }

            byDate[date] = cur;
        }

        return byDate
            .OrderBy(kv => kv.Key)
            .Select(kv => new DailyInput(
                Date: kv.Key,
                MwhDay: kv.Value.Mwh,
                NokDay: kv.Value.Nok,
                SpotDayAvg: kv.Value.SpotCount > 0 ? kv.Value.SpotSum / kv.Value.SpotCount : 0,
                ElhubSpotValueDay: kv.Value.ElhubSpotValue))
            .ToList();
    }

    /// <summary>
    /// Beregner P_low og P_high via lineær interpolasjon (typisk SciPy
    /// "linear"-metode). Identisk med Excel's PERCENTILE-funksjon.
    /// </summary>
    private static (double Low, double High) ComputePercentileBounds(
        IReadOnlyList<double> values, double pLow, double pHigh)
    {
        if (values.Count == 0) return (double.MinValue, double.MaxValue);
        if (values.Count == 1) return (values[0], values[0]);

        var sorted = values.OrderBy(v => v).ToArray();
        return (Percentile(sorted, pLow), Percentile(sorted, pHigh));
    }

    private static double Percentile(double[] sorted, double p)
    {
        // Lineær interpolasjon — matcher Excel PERCENTILE.INC
        var n = sorted.Length;
        var rank = p * (n - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        var fraction = rank - lower;
        return sorted[lower] + fraction * (sorted[upper] - sorted[lower]);
    }

    private static CaptureRateResult Empty() => new(
        CapturePriceNokMwh: 0,
        TimesCr: 0,
        TimesBaselineNokMwh: 0,
        DagCr: 0,
        DagBaselineNokMwh: 0,
        TimingMerverdiNok: 0,
        RealisertPrisNokMwh: 0,
        RealisertVsSpotNok: 0,
        AntallTimer: 0,
        AntallTimerProduksjon: 0,
        AntallDager: 0,
        AntallDagerEtterFilter: 0);
}
