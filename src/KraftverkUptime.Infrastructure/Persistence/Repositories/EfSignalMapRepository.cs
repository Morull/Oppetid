using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Persistence.Repositories;

public sealed class EfSignalMapRepository : ISignalMapRepository
{
    private readonly KraftverkDbContext _db;
    private readonly IQueryContext _queryContext;

    public EfSignalMapRepository(KraftverkDbContext db, IQueryContext queryContext)
    {
        _db = db;
        _queryContext = queryContext;
    }

    public async Task<IReadOnlyList<SignalMap>> ListForPlantAsync(string plantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        var rows = await _queryContext.Apply(_db.SignalMaps.AsQueryable())
            .Where(x => x.PlantId == plantId)
            .OrderBy(x => x.SignalId)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ConvertAll(ToDomain);
    }

    public async Task<SignalMap?> GetAsync(string plantId, string signalId, CancellationToken ct)
    {
        var row = await _queryContext.Apply(_db.SignalMaps.AsQueryable())
            .FirstOrDefaultAsync(x => x.PlantId == plantId && x.SignalId == signalId, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    public async Task<string?> GetSignalIdForRoleAsync(string plantId, SignalRole role, CancellationToken ct)
    {
        return await _queryContext.Apply(_db.SignalMaps.AsQueryable())
            .Where(x => x.PlantId == plantId && x.Role == role && x.IsActive)
            .OrderBy(x => x.SignalId)
            .Select(x => x.SignalId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SignalMap>> GetByPlantDamAndRoleAsync(
        string plantId, string? damId, SignalRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        // Filtrerer på DamId — null i parameteren matches mot rader med null DamId
        // (f.eks. generator-tags som ikke er dam-knyttet).
        var query = _queryContext.Apply(_db.SignalMaps.AsQueryable())
            .Where(x => x.PlantId == plantId && x.Role == role && x.IsActive);
        query = damId is null
            ? query.Where(x => x.DamId == null)
            : query.Where(x => x.DamId == damId);

        var rows = await query
            .OrderBy(x => x.SignalId)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ConvertAll(ToDomain);
    }

    public async Task UpsertAsync(SignalMap signalMap, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signalMap);
        var existing = await _db.SignalMaps
            .FirstOrDefaultAsync(x => x.PlantId == signalMap.PlantId && x.SignalId == signalMap.SignalId, ct)
            .ConfigureAwait(false);
        if (existing is null)
        {
            _db.SignalMaps.Add(new SignalMapEntry
            {
                PlantId = signalMap.PlantId,
                SignalId = signalMap.SignalId,
                CsvColumn = signalMap.CsvColumn,
                Unit = signalMap.Unit,
                Role = signalMap.Role,
                StoreSamples = signalMap.StoreSamples,
                IsActive = signalMap.IsActive,
                DamId = signalMap.DamId,
            });
        }
        else
        {
            existing.CsvColumn = signalMap.CsvColumn;
            existing.Unit = signalMap.Unit;
            existing.Role = signalMap.Role;
            existing.StoreSamples = signalMap.StoreSamples;
            existing.IsActive = signalMap.IsActive;
            existing.DamId = signalMap.DamId;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static SignalMap ToDomain(SignalMapEntry e) => new(
        e.PlantId, e.SignalId, e.CsvColumn, e.Unit, e.Role, e.StoreSamples, e.IsActive, e.DamId);
}
