using KraftverkUptime.Core.Time;

namespace KraftverkUptime.Modules.Reporting.CaptureRate;

/// <summary>
/// Pure-funksjon kalkulator for capture rate (begge varianter):
///
///   <b>Times-CR</b> (volumvektet, hovedversjon):
///     capture_price  = Σ(nok_t) / Σ(mwh_t)            for produksjonstimer
///     times_baseline = Σ(spot_t) / |H_all|            (tidsvektet snitt over alle timer)
///     times_cr       = capture_price / times_baseline
///
///   <b>Dag-CR</b> (Excel-replika med 5/95-persentilfilter):
///     For hver dag: oppnådd_d = nok_d / mwh_d, spot_d = aritmetisk dag-snitt
///                   rå_d      = oppnådd_d / spot_d
///     Filter:       behold dager der rå_d ∈ [P5, P95] beregnet over historisk tidsserie
///     dag_cr        = Σ(mwh_d × rå_d) / Σ(mwh_d)      for filtrerte dager
///
///   <b>Merverdi (NOK)</b>:
///     merverdi = Σ(nok_t − spot_t × mwh_t)            for produksjonstimer
///     (positiv = anlegget tjente mer enn rent spot)
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
    /// (8+ år) for å matche Excel-modellens P5/P95-vinduer over alt anlegget har av historikk.
    /// </summary>
    public sealed record DailyInput(
        DateOnly Date,
        double MwhDay,
        double NokDay,
        double SpotDayAvg);

    public sealed record CaptureRateResult(
        double CapturePriceNokMwh,
        double TimesCr,
        double TimesBaselineNokMwh,
        double DagCr,
        double DagBaselineNokMwh,
        double MerverdiNok,
        int AntallTimer,
        int AntallTimerProduksjon,
        int AntallDager,
        int AntallDagerEtterFilter);

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
        double sumMwh = 0;
        double sumNok = 0;
        double sumSpot = 0;
        var timerProduksjon = 0;
        var timerMedSpot = 0;
        double merverdi = 0;

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
                merverdi += h.SpotomsetningNok.Value - h.SpotprisNokMwh.Value * h.MwhElhub.Value;
                timerProduksjon++;
            }
        }

        var capturePrice = sumMwh > 0 ? sumNok / sumMwh : 0;
        var timesBaseline = timerMedSpot > 0 ? sumSpot / timerMedSpot : 0;
        var timesCr = timesBaseline > 0 ? capturePrice / timesBaseline : 0;

        // ---- Dag-CR med persentilfilter ----
        var (dagCr, dagBaseline, antallDager, antallEtterFilter) = ComputeDagCr(
            hours, historicalDailyForPercentile, percentileLow, percentileHigh);

        return new CaptureRateResult(
            CapturePriceNokMwh: capturePrice,
            TimesCr: timesCr,
            TimesBaselineNokMwh: timesBaseline,
            DagCr: dagCr,
            DagBaselineNokMwh: dagBaseline,
            MerverdiNok: merverdi,
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

        // Beregn rå_d for valgt periode (kun dager med produksjon + spot > 0)
        var rawInPeriod = new List<(DateOnly Date, double Mwh, double Raw)>();
        foreach (var d in dailyInPeriod)
        {
            if (d.MwhDay <= 0 || d.NokDay <= 0 || d.SpotDayAvg <= 0) continue;
            var oppnaadd = d.NokDay / d.MwhDay;
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
            .Where(d => d.MwhDay > 0 && d.NokDay > 0 && d.SpotDayAvg > 0)
            .Select(d => (d.NokDay / d.MwhDay) / d.SpotDayAvg)
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
        var byDate = new Dictionary<DateOnly, (double Mwh, double Nok, double SpotSum, int SpotCount)>();
        foreach (var h in hours)
        {
            var local = TimeZoneInfo.ConvertTime(h.TimeUtc, tz);
            var date = DateOnly.FromDateTime(local.DateTime);
            byDate.TryGetValue(date, out var cur);

            if (h.MwhElhub is > 0) cur.Mwh += h.MwhElhub.Value;
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
                SpotDayAvg: kv.Value.SpotCount > 0 ? kv.Value.SpotSum / kv.Value.SpotCount : 0))
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
        MerverdiNok: 0,
        AntallTimer: 0,
        AntallTimerProduksjon: 0,
        AntallDager: 0,
        AntallDagerEtterFilter: 0);
}
