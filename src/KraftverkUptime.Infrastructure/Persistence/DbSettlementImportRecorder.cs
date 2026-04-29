using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// EF-basert implementasjon av <see cref="ISettlementImportRecorder"/>.
/// Upsert-semantikk: samme (OwnerOrgId, PlantId, IdempotencyKey) oppdaterer
/// eksisterende rad i stedet for å skape en ny.
/// </summary>
public sealed class DbSettlementImportRecorder : ISettlementImportRecorder
{
    private readonly KraftverkDbContext _db;

    public DbSettlementImportRecorder(KraftverkDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public async Task RecordAsync(SettlementImportRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        var existing = await _db.SettlementImports
            .FirstOrDefaultAsync(
                x => x.OwnerOrgId == record.OwnerOrgId
                     && x.PlantId == record.PlantId
                     && x.IdempotencyKey == record.IdempotencyKey,
                ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.SettlementImports.Add(new SettlementImport
            {
                OwnerOrgId = record.OwnerOrgId,
                PlantId = record.PlantId,
                IdempotencyKey = record.IdempotencyKey,
                BlobPath = record.BlobPath,
                PlantName = record.PlantName,
                SchemaVersion = record.SchemaVersion,
                PeriodStartUtc = record.PeriodStartUtc,
                PeriodEndUtc = record.PeriodEndUtc,
                HourCount = record.HourCount,
                IssueCount = record.IssueCount,
                ImportedAtUtc = record.ImportedAtUtc,
                CorrelationId = record.CorrelationId,
            });
        }
        else
        {
            existing.BlobPath = record.BlobPath;
            existing.PlantName = record.PlantName;
            existing.SchemaVersion = record.SchemaVersion;
            existing.PeriodStartUtc = record.PeriodStartUtc;
            existing.PeriodEndUtc = record.PeriodEndUtc;
            existing.HourCount = record.HourCount;
            existing.IssueCount = record.IssueCount;
            existing.ImportedAtUtc = record.ImportedAtUtc;
            existing.CorrelationId = record.CorrelationId;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<SettlementImportRecord?> FindLatestCoveringAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plantId))
        {
            throw new ArgumentException("plantId mangler.", nameof(plantId));
        }

        var row = await _db.SettlementImports
            .AsNoTracking()
            .Where(x => x.PlantId == plantId
                        && x.PeriodStartUtc <= fromUtc
                        && x.PeriodEndUtc >= toUtc)
            .OrderByDescending(x => x.ImportedAtUtc)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null ? null : ToRecord(row);
    }

    public async Task<SettlementImportRecord?> FindByIdempotencyKeyAsync(
        string plantId,
        string idempotencyKey,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var row = await _db.SettlementImports
            .AsNoTracking()
            .Where(x => x.PlantId == plantId && x.IdempotencyKey == idempotencyKey)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null ? null : ToRecord(row);
    }

    public async Task<IReadOnlyList<SettlementImportRecord>> ListForPlantAsync(
        string plantId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var query = _db.SettlementImports
            .AsNoTracking()
            .Where(x => x.PlantId == plantId);

        if (fromUtc.HasValue && toUtc.HasValue)
        {
            // Periode-overlapp: [periodStart, periodEnd] overlapper [from, to]
            // når periodStart <= to OG periodEnd >= from.
            var from = fromUtc.Value;
            var to = toUtc.Value;
            query = query.Where(x => x.PeriodStartUtc <= to && x.PeriodEndUtc >= from);
        }

        var rows = await query
            .OrderByDescending(x => x.ImportedAtUtc)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(ToRecord);
    }

    public async Task<bool> DeleteAsync(string plantId, string idempotencyKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var deleted = await _db.SettlementImports
            .Where(x => x.PlantId == plantId && x.IdempotencyKey == idempotencyKey)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
        return deleted > 0;
    }

    public async Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        return await _db.SettlementImports
            .Where(x => x.PlantId == plantId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteAllAsync(string ownerOrgId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrgId);
        return await _db.SettlementImports
            .Where(x => x.OwnerOrgId == ownerOrgId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static SettlementImportRecord ToRecord(SettlementImport row) => new()
    {
        OwnerOrgId = row.OwnerOrgId,
        PlantId = row.PlantId ?? string.Empty,
        IdempotencyKey = row.IdempotencyKey,
        BlobPath = row.BlobPath,
        PlantName = row.PlantName,
        SchemaVersion = row.SchemaVersion,
        PeriodStartUtc = row.PeriodStartUtc,
        PeriodEndUtc = row.PeriodEndUtc,
        HourCount = row.HourCount,
        IssueCount = row.IssueCount,
        ImportedAtUtc = row.ImportedAtUtc,
        CorrelationId = row.CorrelationId,
    };
}
