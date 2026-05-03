using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.DataQuality;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// EF/blob-implementasjon av <see cref="IDataQualityQueryService"/>.
///
/// Strategi (samme mønster som NedetidQueryService):
///   1. List settlement-imports for plantet i [from, to)
///   2. Last rapport-blob for hver import
///   3. Aggregerer DqState-fordeling fra Classified-rader
///   4. Beregn manglende dekning (timer i perioden uten import)
///   5. Plukk topp-10 ikke-Good rader som "issues" til UI
///
/// Bulk-versjonen (<see cref="GetSummariesForAllPlantsAsync"/>) lister alle
/// anlegg fra <c>core.plants</c> og kaller per-plant-logikken — N+1 ja, men
/// portefølje-kall er sjeldne (ett per side-load på portefolje-siden).
/// </summary>
public sealed class DataQualityQueryService : IDataQualityQueryService
{
    private const int TopIssuesLimit = 10;

    private readonly KraftverkDbContext _db;
    private readonly ISettlementImportRecorder _imports;
    private readonly IUptimeReportStore _reports;
    private readonly ILogger<DataQualityQueryService> _log;

    public DataQualityQueryService(
        KraftverkDbContext db,
        ISettlementImportRecorder imports,
        IUptimeReportStore reports,
        ILogger<DataQualityQueryService> log)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<DataQualitySummary?> GetSummaryAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        if (toUtc <= fromUtc) return null;

        var plant = await _db.Plants.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == plantId, ct).ConfigureAwait(false);
        if (plant is null) return null;

        return await BuildSummaryAsync(plantId, plant.Name, fromUtc, toUtc, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DataQualitySummary>> GetSummariesForAllPlantsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        if (toUtc <= fromUtc) return Array.Empty<DataQualitySummary>();

        var plants = await _db.Plants.AsNoTracking()
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        var results = new List<DataQualitySummary>(plants.Count);
        foreach (var plant in plants)
        {
            var summary = await BuildSummaryAsync(plant.Id, plant.Name, fromUtc, toUtc, ct)
                .ConfigureAwait(false);
            if (summary is not null) results.Add(summary);
        }
        return results;
    }

    private async Task<DataQualitySummary?> BuildSummaryAsync(
        string plantId, string plantName,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var totalHours = (int)Math.Round((toUtc - fromUtc).TotalHours);
        if (totalHours <= 0) return null;

        var imports = await _imports
            .ListForPlantAsync(plantId, fromUtc, toUtc, limit: 500, ct)
            .ConfigureAwait(false);

        if (imports.Count == 0)
        {
            // Ingen imports — vi rapporterer "100 % mangler import" i stedet for
            // null slik at UI kan vise rød indikator i stedet for "ikke vurdert".
            return new DataQualitySummary(
                PlantId: plantId,
                PlantName: plantName,
                FromUtc: fromUtc,
                ToUtc: toUtc,
                TotalHours: totalHours,
                GoodHours: 0,
                WarningHours: 0,
                BadHours: 0,
                MissingHours: 0,
                ManglerImportHours: totalHours,
                GoodPct: 0,
                DekningPct: 0,
                TopIssues: Array.Empty<DataQualityIssue>());
        }

        // Slå sammen Classified-rader fra alle imports som overlapper. Dedupliser
        // på TimeUtc (siste import vinner ved re-import).
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
                hoursByTime[h.TimeUtc] = h;
            }
        }

        if (hoursByTime.Count == 0)
        {
            // Imports finnes men ingen rader dekker perioden — sjelden, men kan
            // skje hvis import-perioden ikke matcher.
            return new DataQualitySummary(
                plantId, plantName, fromUtc, toUtc,
                TotalHours: totalHours,
                GoodHours: 0, WarningHours: 0, BadHours: 0, MissingHours: 0,
                ManglerImportHours: totalHours,
                GoodPct: 0, DekningPct: 0,
                TopIssues: Array.Empty<DataQualityIssue>());
        }

        var good = 0;
        var warning = 0;
        var bad = 0;
        var missing = 0;
        var issues = new List<DataQualityIssue>();
        foreach (var row in hoursByTime.Values.OrderBy(r => r.TimeUtc))
        {
            var state = row.Row.DqState;
            switch (state)
            {
                case DataQualityState.Good:
                    good++;
                    break;
                case DataQualityState.Uncertain:
                case DataQualityState.Substituted:
                    warning++;
                    if (issues.Count < TopIssuesLimit)
                    {
                        issues.Add(new DataQualityIssue(row.TimeUtc, state, BuildReason(row, state)));
                    }
                    break;
                case DataQualityState.Quarantined:
                case DataQualityState.Rejected:
                    bad++;
                    if (issues.Count < TopIssuesLimit)
                    {
                        issues.Add(new DataQualityIssue(row.TimeUtc, state, BuildReason(row, state)));
                    }
                    break;
                case DataQualityState.InformationUnavailable:
                    missing++;
                    if (issues.Count < TopIssuesLimit)
                    {
                        issues.Add(new DataQualityIssue(row.TimeUtc, state, "Ingen settlement-rad for denne timen"));
                    }
                    break;
            }
        }

        var coveredHours = hoursByTime.Count;
        var manglerImport = Math.Max(0, totalHours - coveredHours);
        // Når imports finnes men dekker mer enn forventet (kan skje ved DST),
        // bruker vi coveredHours som total slik at prosenter summerer til 100.
        var effectiveTotal = Math.Max(totalHours, coveredHours + manglerImport);
        var goodPct = effectiveTotal == 0 ? 0 : (double)good / effectiveTotal;
        var dekningPct = effectiveTotal == 0 ? 0 : (double)coveredHours / effectiveTotal;

        _log.LogDebug(
            "DataQuality {PlantId} [{From}, {To}): {Total}t, dekning={Cov}t, good={Good}, warn={Warn}, bad={Bad}, miss={Miss}",
            plantId, fromUtc, toUtc, effectiveTotal, coveredHours, good, warning, bad, missing);

        return new DataQualitySummary(
            PlantId: plantId,
            PlantName: plantName,
            FromUtc: fromUtc,
            ToUtc: toUtc,
            TotalHours: effectiveTotal,
            GoodHours: good,
            WarningHours: warning,
            BadHours: bad,
            MissingHours: missing,
            ManglerImportHours: manglerImport,
            GoodPct: goodPct,
            DekningPct: dekningPct,
            TopIssues: issues);
    }

    /// <summary>
    /// Bygger en menneskelig grunn for hvorfor en time ikke er Good. Brukes som
    /// "Reason"-felt i UI-issue-listen. Ikke i18n-isert ennå — Norge-only-app.
    /// </summary>
    private static string BuildReason(ClassifiedHourlyRow row, DataQualityState state)
    {
        var sigs = new List<string>();
        if (!row.Row.MwhElhub.HasValue) sigs.Add("Elhub mangler");
        else if (row.Row.MwhElhub.Value < 0) sigs.Add($"Elhub negativ ({row.Row.MwhElhub:F2})");
        if (!row.Row.SpotprisNokMwh.HasValue) sigs.Add("Spotpris mangler");

        var reason = state switch
        {
            DataQualityState.Uncertain => "Verdi avviker fra parallelle kilder",
            DataQualityState.Substituted => "Verdi interpolert / beregnet",
            DataQualityState.Quarantined => "Verdi utenfor akseptabelt område",
            DataQualityState.Rejected => "Avvist av plausibilitetskontroll",
            DataQualityState.InformationUnavailable => "Ingen måling",
            _ => state.ToString()
        };

        return sigs.Count == 0 ? reason : $"{reason}: {string.Join(", ", sigs)}";
    }
}
