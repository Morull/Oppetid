using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Annotations.Repositories;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core-implementasjon av <see cref="IDowntimeCategoryRepository"/>.
/// Kategori-listen er lese-tung og kan caches senere; foreløpig direkte
/// DB-spørring per request.
/// </summary>
public sealed class EfDowntimeCategoryRepository : IDowntimeCategoryRepository
{
    private readonly KraftverkDbContext _db;

    public EfDowntimeCategoryRepository(KraftverkDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<DowntimeCategory>> ListActiveAsync(CancellationToken ct)
    {
        var rows = await _db.DowntimeCategories
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.DisplayName)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.ConvertAll(ToDomain);
    }

    public async Task<IReadOnlyList<DowntimeCategory>> ListAllAsync(CancellationToken ct)
    {
        var rows = await _db.DowntimeCategories
            .AsNoTracking()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.DisplayName)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.ConvertAll(ToDomain);
    }

    public async Task<DowntimeCategory?> GetAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var row = await _db.DowntimeCategories
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    private static DowntimeCategory ToDomain(DowntimeCategoryEntry e) => new(
        e.Id,
        e.DisplayName,
        e.ColorHex,
        e.UnitStateOverride,
        e.SortOrder,
        e.IsActive,
        e.IsSystem);
}
