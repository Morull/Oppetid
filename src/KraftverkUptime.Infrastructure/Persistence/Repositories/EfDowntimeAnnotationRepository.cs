using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Annotations.Repositories;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core-implementasjon av <see cref="IDowntimeAnnotationRepository"/>.
/// Bruker query-filter fra DbContext for å skjule slettede rader, og
/// IQueryContext for tenant-/plant-filter (no-op i v1).
/// </summary>
public sealed class EfDowntimeAnnotationRepository : IDowntimeAnnotationRepository
{
    private readonly KraftverkDbContext _db;
    private readonly IQueryContext _queryContext;

    public EfDowntimeAnnotationRepository(KraftverkDbContext db, IQueryContext queryContext)
    {
        _db = db;
        _queryContext = queryContext;
    }

    public async Task<IReadOnlyList<DowntimeAnnotation>> ListAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);

        var rows = await _queryContext
            .Apply(_db.DowntimeAnnotations.AsQueryable())
            .Where(x => x.PlantId == plantId && x.StartUtc < toUtc && x.EndUtc > fromUtc)
            .OrderBy(x => x.StartUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(ToDomain);
    }

    public async Task<DowntimeAnnotation?> GetAsync(long id, CancellationToken ct)
    {
        var row = await _queryContext
            .Apply(_db.DowntimeAnnotations.AsQueryable())
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            .ConfigureAwait(false);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<DowntimeAnnotation>> FindOverlappingAsync(
        string plantId, DateTimeOffset startUtc, DateTimeOffset endUtc, long? excludeId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);

        var query = _queryContext
            .Apply(_db.DowntimeAnnotations.AsQueryable())
            .Where(x => x.PlantId == plantId && x.StartUtc < endUtc && x.EndUtc > startUtc);

        if (excludeId.HasValue)
        {
            var id = excludeId.Value;
            query = query.Where(x => x.Id != id);
        }

        var rows = await query.OrderBy(x => x.StartUtc).ToListAsync(ct).ConfigureAwait(false);
        return rows.ConvertAll(ToDomain);
    }

    public async Task<long> CreateAsync(DowntimeAnnotation annotation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        var entry = new DowntimeAnnotationEntry
        {
            OwnerOrgId = annotation.OwnerOrgId,
            PlantId = annotation.PlantId,
            StartUtc = annotation.StartUtc,
            EndUtc = annotation.EndUtc,
            CategoryId = annotation.CategoryId,
            Comment = annotation.Comment,
            CreatedAt = annotation.CreatedAt,
            CreatedBy = annotation.CreatedBy,
            UpdatedAt = annotation.UpdatedAt,
            UpdatedBy = annotation.UpdatedBy
        };

        _db.DowntimeAnnotations.Add(entry);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entry.Id;
    }

    public async Task<DowntimeAnnotation?> UpdateAsync(
        long id,
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        string? categoryId,
        string? comment,
        string? updatedBy,
        CancellationToken ct)
    {
        var row = await _queryContext
            .Apply(_db.DowntimeAnnotations.AsQueryable())
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        if (startUtc.HasValue)
        {
            row.StartUtc = startUtc.Value;
        }
        if (endUtc.HasValue)
        {
            row.EndUtc = endUtc.Value;
        }
        if (categoryId is not null)
        {
            row.CategoryId = categoryId;
        }
        if (comment is not null)
        {
            // tom streng = klienten vil tømme kommentaren
            row.Comment = string.IsNullOrEmpty(comment) ? null : comment;
        }
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = updatedBy;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToDomain(row);
    }

    public async Task<bool> SoftDeleteAsync(long id, string? deletedBy, CancellationToken ct)
    {
        var row = await _queryContext
            .Apply(_db.DowntimeAnnotations.AsQueryable())
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            .ConfigureAwait(false);
        if (row is null)
        {
            return false;
        }

        row.DeletedAt = DateTimeOffset.UtcNow;
        row.DeletedBy = deletedBy;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static DowntimeAnnotation ToDomain(DowntimeAnnotationEntry e) => new(
        e.Id,
        e.OwnerOrgId,
        e.PlantId ?? string.Empty,
        e.StartUtc,
        e.EndUtc,
        e.CategoryId,
        e.Comment,
        e.CreatedAt,
        e.CreatedBy,
        e.UpdatedAt,
        e.UpdatedBy,
        e.DeletedAt,
        e.DeletedBy);
}
