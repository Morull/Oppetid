using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.Produksjon;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Persistence;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Aggregerer settlement-rader på tvers av imports for en periode og
/// beregner produksjons-KPI-er via <see cref="ProduksjonAnalyseCalculator"/>.
/// Bruker samme load-mønster som <see cref="CaptureRateQueryService"/> —
/// itererer over overlappende imports og leser UptimeReport-blobs.
/// </summary>
public sealed class ProduksjonAnalyseQueryService : IProduksjonAnalyseService
{
    private readonly ISettlementImportRecorder _imports;
    private readonly IUptimeReportStore _reports;

    public ProduksjonAnalyseQueryService(
        ISettlementImportRecorder imports,
        IUptimeReportStore reports)
    {
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
    }

    public async Task<ProduksjonAnalyseResult> GetAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        if (toUtc <= fromUtc)
        {
            return ProduksjonAnalyseCalculator.Compute(
                plantId, fromUtc, toUtc, Array.Empty<ProduksjonAnalyseCalculator.HourlyInput>());
        }

        var imports = await _imports
            .ListForPlantAsync(plantId, fromUtc, toUtc, limit: 500, ct)
            .ConfigureAwait(false);
        if (imports.Count == 0)
        {
            return ProduksjonAnalyseCalculator.Compute(
                plantId, fromUtc, toUtc, Array.Empty<ProduksjonAnalyseCalculator.HourlyInput>());
        }

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

        var input = hoursByTime.Values
            .OrderBy(h => h.TimeUtc)
            .Select(h => new ProduksjonAnalyseCalculator.HourlyInput(
                TimeUtc: h.TimeUtc,
                PlanMwh: h.Row.ProduksjonplanMwh,
                ElhubMwh: h.Row.MwhElhub,
                SpotprisNokMwh: h.Row.SpotprisNokMwh))
            .ToList();

        return ProduksjonAnalyseCalculator.Compute(plantId, fromUtc, toUtc, input);
    }
}
