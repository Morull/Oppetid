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

        if (row is null)
        {
            return null;
        }

        return new SettlementImportRecord
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
}
