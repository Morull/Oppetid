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

    public async Task AddAsync(DowntimeCategory category, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(category);
        var entry = new DowntimeCategoryEntry
        {
            Id = category.Id,
            DisplayName = category.DisplayName,
            ColorHex = category.ColorHex,
            UnitStateOverride = category.UnitStateOverride,
            SortOrder = category.SortOrder,
            IsActive = category.IsActive,
            IsSystem = false, // Brukerdefinerte kategorier er aldri system
        };
        _db.DowntimeCategories.Add(entry);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(DowntimeCategory category, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(category);
        var existing = await _db.DowntimeCategories
            .FirstOrDefaultAsync(x => x.Id == category.Id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            throw new InvalidOperationException(
                $"Kategori '{category.Id}' finnes ikke — kan ikke oppdatere.");
        }

        existing.DisplayName = category.DisplayName;
        existing.ColorHex = category.ColorHex;
        existing.SortOrder = category.SortOrder;
        existing.IsActive = category.IsActive;

        // System-kategorier får IKKE endre UnitStateOverride — overlay-koden
        // antar at fault → ForcedOutage osv. Brukerdefinerte kategorier kan
        // velge fritt.
        if (!existing.IsSystem)
        {
            existing.UnitStateOverride = category.UnitStateOverride;
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var existing = await _db.DowntimeCategories
            .FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (existing is null) return;
        if (existing.IsSystem)
        {
            throw new InvalidOperationException(
                $"Kategori '{id}' er system-kategori og kan ikke slettes. Bruk IsActive=false for å gjemme den.");
        }

        var refs = await _db.DowntimeAnnotations
            .Where(a => a.CategoryId == id && a.DeletedAt == null)
            .CountAsync(ct).ConfigureAwait(false);
        if (refs > 0)
        {
            throw new InvalidOperationException(
                $"Kategori '{id}' er referert til av {refs} annoteringer. Slett eller flytt dem først.");
        }

        _db.DowntimeCategories.Remove(existing);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
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
