using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
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

        // Hent alle imports som overlapper [fromMonth, toMonth). En import
        // kan spenne flere måneder (eks. SCADA-fil for jan-mai), så vi må
        // generere én celle per (plant, source, måned-i-import-spennet).
        var imports = await _db.DataImports
            .AsNoTracking()
            .Where(i => i.PeriodFromUtc < toMonth && i.PeriodToUtc > fromMonth)
            .ToListAsync(ct).ConfigureAwait(false);

        // Splitt hver import til alle månedene den dekker, og aggreger per
        // (plant, source, måned). "Siste import vinner" hvis flere imports
        // dekker samme måned.
        //
        // Per-måned dekning: hvis en import ikke dekker hele måneden (eks.
        // eksport fra Jan 15 til Mai 15), beregner vi overlap-andel av
        // måneden og ganger med import.coverage_pct. Da:
        //   Jan (15.-31.) = 16/31 = 0.52 × import.coverage = 0.52 × 1.0 = 0.52 → PARTIAL
        //   Feb-Apr (full) = 1.0 × 1.0 = 1.0 → COMPLETE
        //   Mai (1.-15.)  = 15/31 = 0.48 × 1.0 = 0.48 → PARTIAL
        // Brukeren får mer presist bilde av hvor data faktisk dekker.
        var importsByKey = new Dictionary<DataCompletenessKey,
            (DataImport Last, double MonthCoverage, int Count)>();
        foreach (var import in imports)
        {
            var startMonth = TruncToMonth(import.PeriodFromUtc);
            // PeriodToUtc er eksklusiv. Trekk fra ett tick for å finne
            // siste måned som faktisk har data.
            var lastDataPoint = import.PeriodToUtc.AddTicks(-1);
            var endMonth = TruncToMonth(lastDataPoint);
            var rawCoverage = import.CoveragePct ?? 1.0;

            for (var m = startMonth; m <= endMonth; m = m.AddMonths(1))
            {
                if (m >= toMonth) break;
                if (m < fromMonth) continue;

                // Beregn hvor stor del av denne måneden som er innenfor importens spenn
                var monthEnd = m.AddMonths(1);
                var overlapStart = import.PeriodFromUtc > m ? import.PeriodFromUtc : m;
                var overlapEnd = import.PeriodToUtc < monthEnd ? import.PeriodToUtc : monthEnd;
                var overlapHours = (overlapEnd - overlapStart).TotalHours;
                var monthHours = (monthEnd - m).TotalHours;
                var monthOverlapFraction = monthHours > 0
                    ? Math.Clamp(overlapHours / monthHours, 0.0, 1.0)
                    : 1.0;
                var monthCoverage = Math.Clamp(rawCoverage * monthOverlapFraction, 0.0, 1.0);

                var key = new DataCompletenessKey(import.PlantId, import.SourceType, m);
                if (importsByKey.TryGetValue(key, out var existing))
                {
                    // Siste import vinner. Hvis to imports dekker samme måned
                    // (eks. én januar-snapshot + én jan-mai-eksport), ta den
                    // med høyest dekning — gir mest realistisk bilde.
                    var keepNew = import.ImportedAtUtc > existing.Last.ImportedAtUtc
                                  && monthCoverage >= existing.MonthCoverage * 0.9;
                    if (keepNew)
                    {
                        importsByKey[key] = (import, monthCoverage, existing.Count + 1);
                    }
                    else if (monthCoverage > existing.MonthCoverage)
                    {
                        // Ny import har bedre dekning men er eldre — bruk dens
                        // dekning men behold den eldres "Last"-metadata for
                        // konsistens (timestamp i tooltip viser når dataen kom inn).
                        importsByKey[key] = (existing.Last, monthCoverage, existing.Count + 1);
                    }
                    else
                    {
                        importsByKey[key] = (existing.Last, existing.MonthCoverage, existing.Count + 1);
                    }
                }
                else
                {
                    importsByKey[key] = (import, monthCoverage, 1);
                }
            }
        }

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
                    // Bruk per-måned-beregnet dekning (justert for hvor stor del
                    // av måneden som er dekket av import-spennet) i stedet for
                    // import-rådekning. Gir mer realistisk bilde for offset-
                    // perioder (eks. eksport fra 15. januar har bare halv januar).
                    var coverage = hit.MonthCoverage;
                    var threshold = exp.CompletionThresholdPct > 0
                        ? exp.CompletionThresholdPct
                        : CompleteCoverageThreshold;
                    var status = coverage < threshold ? "PARTIAL" : "COMPLETE";
                    cell = new DataCompletenessCell(
                        Status: status,
                        LastImportedAt: hit.Last.ImportedAtUtc,
                        CoveragePct: coverage,
                        ImportCount: hit.Count);
                }
                else
                {
                    var lagCutoff = periodEnd.AddDays(exp.ExpectedLagDays);
                    if (now > lagCutoff)
                    {
                        cell = new DataCompletenessCell(
                            Status: "OVERDUE",
                            LastImportedAt: null,
                            CoveragePct: null,
                            ImportCount: 0);
                    }
                    else
                    {
                        // PENDING (innenfor lag-vindu) er irrelevant for
                        // drifts-leder per 2026-05-03-bekreftelse — vi
                        // hopper helt over disse cellene i matrisen og i
                        // sammendraget. Cellen er fortsatt forventet, men
                        // ikke noe brukeren skal handle på.
                        continue;
                    }
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
