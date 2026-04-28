using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Persistence.Repositories;

public sealed class EfClassifiedEventRepository : IClassifiedEventRepository
{
    private readonly KraftverkDbContext _db;
    private readonly IQueryContext _queryContext;

    public EfClassifiedEventRepository(KraftverkDbContext db, IQueryContext queryContext)
    {
        _db = db;
        _queryContext = queryContext;
    }

    public async Task<IReadOnlyList<ClassifiedEvent>> ListAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        var rows = await _queryContext.Apply(_db.ClassifiedEvents.AsQueryable())
            .Where(x => x.PlantId == plantId && x.StartUtc < toUtc && (x.EndUtc == null || x.EndUtc > fromUtc))
            .OrderBy(x => x.StartUtc)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ConvertAll(ToDomain);
    }

    public async Task<long> CreateAsync(ClassifiedEvent ev, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var entry = new ClassifiedEventEntry
        {
            OwnerOrgId = ev.OwnerOrgId,
            PlantId = ev.PlantId,
            StartUtc = ev.StartUtc,
            EndUtc = ev.EndUtc,
            State = ev.State,
            CauseCode = ev.CauseCode,
            Confidence = ev.Confidence,
            SourcesJson = ev.SourcesJson,
            Rationale = ev.Rationale,
        };
        _db.ClassifiedEvents.Add(entry);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entry.Id;
    }

    public async Task<bool> CloseAsync(long id, DateTimeOffset endUtc, CancellationToken ct)
    {
        var row = await _queryContext.Apply(_db.ClassifiedEvents.AsQueryable())
            .FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (row is null) return false;
        row.EndUtc = endUtc;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task UpsertManyAsync(IReadOnlyCollection<ClassifiedEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0) return;

        // Dedupliser på (plant_id, start_utc, state) — typisk for operlog-import.
        var keys = events.Select(e => (e.PlantId, e.StartUtc, e.State)).ToHashSet();
        var plantIds = events.Select(e => e.PlantId).Distinct().ToList();
        var minStart = events.Min(e => e.StartUtc);
        var maxStart = events.Max(e => e.StartUtc);

        var existing = await _db.ClassifiedEvents
            .Where(x => plantIds.Contains(x.PlantId) && x.StartUtc >= minStart && x.StartUtc <= maxStart)
            .ToListAsync(ct).ConfigureAwait(false);
        var existingKeys = existing.Select(e => (e.PlantId, e.StartUtc, e.State)).ToHashSet();

        foreach (var ev in events)
        {
            if (existingKeys.Contains((ev.PlantId, ev.StartUtc, ev.State)))
            {
                continue; // skip duplicate
            }
            _db.ClassifiedEvents.Add(new ClassifiedEventEntry
            {
                OwnerOrgId = ev.OwnerOrgId,
                PlantId = ev.PlantId,
                StartUtc = ev.StartUtc,
                EndUtc = ev.EndUtc,
                State = ev.State,
                CauseCode = ev.CauseCode,
                Confidence = ev.Confidence,
                SourcesJson = ev.SourcesJson,
                Rationale = ev.Rationale,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static ClassifiedEvent ToDomain(ClassifiedEventEntry e) => new(
        e.Id, e.OwnerOrgId, e.PlantId, e.StartUtc, e.EndUtc,
        e.State, e.CauseCode, e.Confidence, e.SourcesJson, e.Rationale);
}
