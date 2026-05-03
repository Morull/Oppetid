using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.DataCompleteness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// EF-implementasjon av <see cref="IDataCompletenessQueryService"/>. Bygger
/// matrisen ved å (1) generere alle forventede (plant, source, period)-
/// kombinasjoner ut fra <c>data_source_expectations</c>, og (2) venstre-
/// joine mot <c>data_imports</c> aggregert per periode.
///
/// Periode-granularitet er måned i v1 — alle perioder anker på månedsstart
/// UTC. SPEC-IMPORT-COMPLETENESS gir fleksibilitet til å introdusere uke
/// eller dag senere; det krever endring i status-beregningen for cadence != monthly.
///
/// Status-regler matcher spec-en:
///   - Ingen import + period_to + lag &lt; now → OVERDUE
///   - Ingen import (ellers) → PENDING
///   - Coverage &lt; 0.95 → PARTIAL
///   - Ellers → COMPLETE
/// </summary>
public sealed class DataCompletenessQueryService : IDataCompletenessQueryService
{
    private const double CompleteCoverageThreshold = 0.95;

    private readonly KraftverkDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<DataCompletenessQueryService> _log;

    public DataCompletenessQueryService(
        KraftverkDbContext db,
        TimeProvider clock,
        ILogger<DataCompletenessQueryService> log)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<DataCompletenessMatrix> GetMatrixAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        if (toUtc <= fromUtc)
        {
            return EmptyMatrix(fromUtc, toUtc);
        }

        var now = _clock.GetUtcNow();
        var fromMonth = TruncToMonth(fromUtc);
        var toMonth = TruncToMonth(toUtc);

        // Generer perioder (månedsstart-tidspunkter) i [from, to].
        var periods = new List<DateTimeOffset>();
        for (var p = fromMonth; p < toMonth; p = p.AddMonths(1))
        {
            periods.Add(p);
        }
        if (periods.Count == 0)
        {
            periods.Add(fromMonth);
        }

        // Hent aktive expectations (alle anlegg som har minst én aktiv kilde
        // listes i PlantIds, men celler genereres bare for aktive kilder).
        var expectations = await _db.DataSourceExpectations
            .AsNoTracking()
            .Where(e => e.IsActive)
            .ToListAsync(ct).ConfigureAwait(false);

        if (expectations.Count == 0)
        {
            return EmptyMatrix(fromMonth, toMonth);
        }

        // Hent alle relevante imports i ett kall, deretter aggreger per
        // (plant, source, period). Vi bruker "siste import vinner" — én rad
        // per periode med max(imported_at_utc) og siste coverage_pct.
        var imports = await _db.DataImports
            .AsNoTracking()
            .Where(i => i.PeriodFromUtc >= fromMonth && i.PeriodFromUtc < toMonth)
            .ToListAsync(ct).ConfigureAwait(false);

        var importsByKey = imports
            .GroupBy(i => new DataCompletenessKey(i.PlantId, i.SourceType, TruncToMonth(i.PeriodFromUtc)))
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var ordered = g.OrderByDescending(i => i.ImportedAtUtc).ToList();
                    return new
                    {
                        Last = ordered[0],
                        Count = ordered.Count,
                    };
                });

        var cells = new Dictionary<DataCompletenessKey, DataCompletenessCell>();
        foreach (var exp in expectations)
        {
            // Begrens til perioder etter activated_at_utc (vi ber ikke om
            // data fra før kilden ble aktivert).
            var earliestPeriod = exp.ActivatedAtUtc is null
                ? fromMonth
                : TruncToMonth(exp.ActivatedAtUtc.Value);

            foreach (var period in periods)
            {
                if (period < earliestPeriod) continue;

                var key = new DataCompletenessKey(exp.PlantId, exp.SourceType, period);
                var periodEnd = period.AddMonths(1);

                DataCompletenessCell cell;
                if (importsByKey.TryGetValue(key, out var hit))
                {
                    var coverage = hit.Last.CoveragePct ?? 1.0;
                    var status = coverage < CompleteCoverageThreshold ? "PARTIAL" : "COMPLETE";
                    cell = new DataCompletenessCell(
                        Status: status,
                        LastImportedAt: hit.Last.ImportedAtUtc,
                        CoveragePct: coverage,
                        ImportCount: hit.Count);
                }
                else
                {
                    var lagCutoff = periodEnd.AddDays(exp.ExpectedLagDays);
                    var status = now > lagCutoff ? "OVERDUE" : "PENDING";
                    cell = new DataCompletenessCell(
                        Status: status,
                        LastImportedAt: null,
                        CoveragePct: null,
                        ImportCount: 0);
                }
                cells[key] = cell;
            }
        }

        var plantIds = expectations.Select(e => e.PlantId).Distinct().OrderBy(p => p).ToList();
        var sourceTypes = expectations.Select(e => e.SourceType).Distinct().OrderBy(s => s).ToList();

        _log.LogDebug(
            "DataCompleteness-matrise: {Plants} anlegg, {Sources} kilder, {Periods} perioder, {Cells} celler",
            plantIds.Count, sourceTypes.Count, periods.Count, cells.Count);

        return new DataCompletenessMatrix(
            PlantIds: plantIds,
            SourceTypes: sourceTypes,
            Periods: periods,
            Cells: cells);
    }

    public async Task<IReadOnlyList<MissingImport>> GetOverdueAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        // Vindu: 12 måneder bakover. Lengre siktet historikk vises i full
        // matrise og er sjelden interessant for "hva ligger og venter"-listen.
        var fromUtc = TruncToMonth(now).AddMonths(-12);
        var toUtc = TruncToMonth(now).AddMonths(1);

        var matrix = await GetMatrixAsync(fromUtc, toUtc, ct).ConfigureAwait(false);
        return matrix.Cells
            .Where(kv => kv.Value.Status == "OVERDUE")
            .Select(kv =>
            {
                var lagDays = kv.Key.Period.AddMonths(1);
                var daysOverdue = (int)Math.Floor((now - lagDays).TotalDays);
                return new MissingImport(
                    PlantId: kv.Key.PlantId,
                    SourceType: kv.Key.SourceType,
                    PeriodFromUtc: kv.Key.Period,
                    DaysOverdue: Math.Max(0, daysOverdue));
            })
            .OrderByDescending(m => m.DaysOverdue)
            .ToList();
    }

    public async Task<DataCompletenessSummary> GetWeeklySummaryAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        // Digest-vindu: forrige måned + denne måneden — det er der
        // "manglende" og "delvis" er mest relevant. Lengre historikk er
        // typisk arkivert og rapport sees i full matrise.
        var fromUtc = TruncToMonth(now).AddMonths(-1);
        var toUtc = TruncToMonth(now).AddMonths(1);

        var matrix = await GetMatrixAsync(fromUtc, toUtc, ct).ConfigureAwait(false);
        var cells = matrix.Cells.Values.ToList();

        var complete = cells.Count(c => c.Status == "COMPLETE");
        var partial = cells.Count(c => c.Status == "PARTIAL");
        var pending = cells.Count(c => c.Status == "PENDING");
        var overdue = cells.Count(c => c.Status == "OVERDUE");

        var topOverdue = matrix.Cells
            .Where(kv => kv.Value.Status == "OVERDUE")
            .Select(kv =>
            {
                var lagDays = kv.Key.Period.AddMonths(1);
                var daysOverdue = (int)Math.Floor((now - lagDays).TotalDays);
                return new MissingImport(
                    PlantId: kv.Key.PlantId,
                    SourceType: kv.Key.SourceType,
                    PeriodFromUtc: kv.Key.Period,
                    DaysOverdue: Math.Max(0, daysOverdue));
            })
            .OrderByDescending(m => m.DaysOverdue)
            .Take(5)
            .ToList();

        return new DataCompletenessSummary(
            TotalExpected: cells.Count,
            Complete: complete,
            Partial: partial,
            Pending: pending,
            Overdue: overdue,
            TopOverdue: topOverdue);
    }

    public async Task<IReadOnlyList<RecentImport>> GetRecentImportsAsync(
        TimeSpan window, int limit, CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow() - window;
        var rows = await _db.DataImports
            .AsNoTracking()
            .Where(i => i.ImportedAtUtc >= cutoff)
            .OrderByDescending(i => i.ImportedAtUtc)
            .Take(limit)
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(i => new RecentImport(
            ImportId: i.ImportId,
            PlantId: i.PlantId,
            SourceType: i.SourceType,
            PeriodFromUtc: i.PeriodFromUtc,
            PeriodToUtc: i.PeriodToUtc,
            ImportedAtUtc: i.ImportedAtUtc,
            FileName: i.FileName,
            RowsImported: i.RowsImported,
            CoveragePct: i.CoveragePct,
            UserId: i.UserId)).ToList();
    }

    private static DateTimeOffset TruncToMonth(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static DataCompletenessMatrix EmptyMatrix(DateTimeOffset fromUtc, DateTimeOffset toUtc)
        => new(
            PlantIds: Array.Empty<string>(),
            SourceTypes: Array.Empty<string>(),
            Periods: Array.Empty<DateTimeOffset>(),
            Cells: new Dictionary<DataCompletenessKey, DataCompletenessCell>());
}
