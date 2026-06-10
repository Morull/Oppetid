using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.CaptureRate;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Henter capture rate via UptimeReport-blobs (samme datakilde som Nedetid og Portefølje).
/// Persentilberegningen for dag-CR bruker hele anleggets historikk så filteret matcher
/// Excel-modellen.
/// </summary>
public sealed class CaptureRateQueryService : ICaptureRateQueryService
{
    private readonly ISettlementImportRecorder _imports;
    private readonly IUptimeReportStore _reports;
    private readonly ILogger<CaptureRateQueryService> _log;

    public CaptureRateQueryService(
        ISettlementImportRecorder imports,
        IUptimeReportStore reports,
        ILogger<CaptureRateQueryService> log)
    {
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<CaptureRateCalculator.CaptureRateResult> GetForPlantAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        if (toUtc <= fromUtc)
        {
            return EmptyResult();
        }

        var hours = await LoadHourlyAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
        var historical = await LoadHistoricalDailyAsync(plantId, ct).ConfigureAwait(false);

        return CaptureRateCalculator.Compute(hours, historical);
    }

    public async Task<IReadOnlyList<MonthlyCaptureRate>> GetMonthlySeriesAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        if (toUtc <= fromUtc) return Array.Empty<MonthlyCaptureRate>();

        var allHours = await LoadHourlyAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
        var historical = await LoadHistoricalDailyAsync(plantId, ct).ConfigureAwait(false);

        var grouped = allHours
            .GroupBy(h => new { h.TimeUtc.Year, h.TimeUtc.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month);

        var result = new List<MonthlyCaptureRate>();
        foreach (var grp in grouped)
        {
            var monthRows = grp.ToList();
            var cr = CaptureRateCalculator.Compute(monthRows, historical);
            result.Add(new MonthlyCaptureRate(grp.Key.Year, grp.Key.Month, cr));
        }
        return result;
    }

    public async Task<IReadOnlyList<DailyCaptureRate>> GetDailySeriesAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        if (toUtc <= fromUtc) return Array.Empty<DailyCaptureRate>();

        var hours = await LoadHourlyAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
        if (hours.Count == 0) return Array.Empty<DailyCaptureRate>();

        var historical = await LoadHistoricalDailyAsync(plantId, ct).ConfigureAwait(false);
        // Grunnlag = Σ(MWh × spot) / Σ(MWh), IKKE NokDay/MwhDay. Dag-CR-KPI-en
        // (CaptureRateCalculator.ComputeDagCr) bruker ElhubSpotValueDay; scatter/
        // histogram må bruke samme teller ellers motsier grafen KPI-kortet
        // (FAGVURDERING-KPI-BEREGNINGER #4, samme feilklasse som Øgreyfoss ~9 pp).
        var historicalRaws = historical
            .Where(d => d.MwhDay > 0 && d.ElhubSpotValueDay > 0 && d.SpotDayAvg > 0)
            .Select(d => (d.ElhubSpotValueDay / d.MwhDay) / d.SpotDayAvg)
            .OrderBy(v => v)
            .ToList();

        // Persentil-grenser: bruk historisk hvis tilgjengelig, ellers periode-data
        var (low, high) = ComputePercentileBounds(historicalRaws, 0.05, 0.95);

        // Aggreger valgt periode til dager
        var daily = AggregateDaily(hours);
        var result = new List<DailyCaptureRate>(daily.Count);
        foreach (var d in daily)
        {
            if (d.MwhDay <= 0 || d.ElhubSpotValueDay <= 0 || d.SpotDayAvg <= 0)
            {
                result.Add(new DailyCaptureRate(
                    Date: d.Date, MwhDay: d.MwhDay, SpotDayAvgNokMwh: d.SpotDayAvg,
                    OppnaaddNokMwh: 0, RaCr: 0, ErFiltrert: true));
                continue;
            }
            var oppnaadd = d.ElhubSpotValueDay / d.MwhDay;
            var raw = oppnaadd / d.SpotDayAvg;
            var filtrert = !(raw >= low && raw <= high);
            result.Add(new DailyCaptureRate(
                Date: d.Date, MwhDay: d.MwhDay, SpotDayAvgNokMwh: d.SpotDayAvg,
                OppnaaddNokMwh: oppnaadd, RaCr: raw, ErFiltrert: filtrert));
        }
        return result;
    }

    /// <summary>
    /// Henter alle settlement-timer for plantet i [from, to) ved å iterere over
    /// overlappende imports og lese UptimeReport-blobs. Identisk strategi som
    /// NedetidQueryService.
    /// </summary>
    private async Task<IReadOnlyList<CaptureRateCalculator.HourlyInput>> LoadHourlyAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var imports = await _imports
            .ListForPlantAsync(plantId, fromUtc, toUtc, limit: 500, ct)
            .ConfigureAwait(false);
        if (imports.Count == 0) return Array.Empty<CaptureRateCalculator.HourlyInput>();

        var hoursByTime = new Dictionary<DateTimeOffset, ClassifiedHourlyRow>();
        foreach (var imp in imports.OrderBy(i => i.ImportedAtUtc))
        {
            var report = await _reports
                .GetAsync(imp.OwnerOrgId, imp.PlantId, imp.IdempotencyKey, ct)
                .ConfigureAwait(false);
            if (report is null) continue;

            foreach (var h in report.Classified)
            {
                if (h.TimeUtc < fromUtc || h.TimeUtc >= toUtc) continue;
                hoursByTime[h.TimeUtc] = h; // siste import vinner ved overlapp
            }
        }

        return hoursByTime.Values
            .OrderBy(h => h.TimeUtc)
            .Select(h => new CaptureRateCalculator.HourlyInput(
                TimeUtc: h.TimeUtc,
                MwhElhub: h.Row.MwhElhub,
                SpotprisNokMwh: h.Row.SpotprisNokMwh,
                SpotomsetningNok: h.Row.SpotomsetningNok))
            .ToList();
    }

    /// <summary>
    /// Henter hele anleggets historikk og aggregerer til daglige rader for
    /// persentil-beregning. Brukes som baseline-vindu for dag-CR-filteret.
    /// </summary>
    private async Task<IReadOnlyList<CaptureRateCalculator.DailyInput>> LoadHistoricalDailyAsync(
        string plantId, CancellationToken ct)
    {
        var imports = await _imports
            .ListForPlantAsync(plantId, fromUtc: null, toUtc: null, limit: 500, ct)
            .ConfigureAwait(false);
        if (imports.Count == 0) return Array.Empty<CaptureRateCalculator.DailyInput>();

        var hoursByTime = new Dictionary<DateTimeOffset, ClassifiedHourlyRow>();
        foreach (var imp in imports.OrderBy(i => i.ImportedAtUtc))
        {
            var report = await _reports
                .GetAsync(imp.OwnerOrgId, imp.PlantId, imp.IdempotencyKey, ct)
                .ConfigureAwait(false);
            if (report is null) continue;

            foreach (var h in report.Classified)
            {
                hoursByTime[h.TimeUtc] = h;
            }
        }

        var hourly = hoursByTime.Values
            .Select(h => new CaptureRateCalculator.HourlyInput(
                h.TimeUtc, h.Row.MwhElhub, h.Row.SpotprisNokMwh, h.Row.SpotomsetningNok))
            .ToList();

        return AggregateDaily(hourly);
    }

    private static IReadOnlyList<CaptureRateCalculator.DailyInput> AggregateDaily(
        IReadOnlyList<CaptureRateCalculator.HourlyInput> hours)
    {
        var tz = KraftverkUptime.Core.Time.TimeZones.Norway;
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
                    // Σ(MWh × spot) — telleren i den nye dag-CR-en (Spec CR-MERVERDI-OPPRYDDING).
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
            .Select(kv => new CaptureRateCalculator.DailyInput(
                Date: kv.Key, MwhDay: kv.Value.Mwh, NokDay: kv.Value.Nok,
                SpotDayAvg: kv.Value.SpotCount > 0 ? kv.Value.SpotSum / kv.Value.SpotCount : 0,
                ElhubSpotValueDay: kv.Value.ElhubSpotValue))
            .ToList();
    }

    private static (double Low, double High) ComputePercentileBounds(
        IReadOnlyList<double> sortedValues, double pLow, double pHigh)
    {
        if (sortedValues.Count == 0) return (double.MinValue, double.MaxValue);
        if (sortedValues.Count == 1) return (sortedValues[0], sortedValues[0]);

        return (Percentile(sortedValues, pLow), Percentile(sortedValues, pHigh));
    }

    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        var n = sorted.Count;
        var rank = p * (n - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        var fraction = rank - lower;
        return sorted[lower] + fraction * (sorted[upper] - sorted[lower]);
    }

    private static CaptureRateCalculator.CaptureRateResult EmptyResult() => new(
        CapturePriceNokMwh: 0, TimesCr: 0, TimesBaselineNokMwh: 0,
        DagCr: 0, DagBaselineNokMwh: 0,
        TimingMerverdiNok: 0, RealisertPrisNokMwh: 0, RealisertVsSpotNok: 0,
        AntallTimer: 0, AntallTimerProduksjon: 0,
        AntallDager: 0, AntallDagerEtterFilter: 0);
}
