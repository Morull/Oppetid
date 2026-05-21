using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF-implementasjon av <see cref="IScadaSampleFineRepository"/>. Speiler
/// <see cref="EfScadaSampleRepository"/> bit for bit, men opererer mot
/// <c>core.sample_facts_fine</c> (15-min-pipelinen) i stedet for
/// <c>core.sample_facts</c> (hourly). Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md
/// — separat lagring hindrer at de to pipelinene overskriver hverandre.
/// </summary>
public sealed class EfScadaSampleFineRepository : IScadaSampleFineRepository
{
    private readonly KraftverkDbContext _db;

    public EfScadaSampleFineRepository(KraftverkDbContext db)
    {
        _db = db;
    }

    public async Task<int> BulkInsertAsync(IReadOnlyCollection<ScadaSample> samples, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) return 0;

        // Dedupliser input mot (AssetId, SignalId, TimeUtc) — siste verdi vinner.
        var deduped = new Dictionary<(string, string, DateTimeOffset), ScadaSample>(samples.Count);
        foreach (var s in samples)
        {
            deduped[(s.AssetId, s.SignalId, s.TimeUtc)] = s;
        }

        // Idempotens via upsert: vi sletter eksisterende rader for de
        // (asset_id, signal_id, time_utc)-kombinasjonene vi skal skrive,
        // og setter dem inn på nytt.
        var assetIds = deduped.Values.Select(s => s.AssetId).Distinct().ToList();
        var minTime = deduped.Values.Min(s => s.TimeUtc);
        var maxTime = deduped.Values.Max(s => s.TimeUtc);

        var existing = await _db.SampleFactsFine
            .Where(x => assetIds.Contains(x.AssetId) && x.TimeUtc >= minTime && x.TimeUtc <= maxTime)
            .ToListAsync(ct).ConfigureAwait(false);

        var keys = new HashSet<(string, string, DateTimeOffset)>(deduped.Keys);
        var toRemove = existing.Where(e => keys.Contains((e.AssetId, e.SignalId, e.TimeUtc))).ToList();
        if (toRemove.Count > 0)
        {
            _db.SampleFactsFine.RemoveRange(toRemove);
        }

        foreach (var s in deduped.Values)
        {
            _db.SampleFactsFine.Add(new SampleFactFineEntry
            {
                AssetId = s.AssetId,
                SignalId = s.SignalId,
                TimeUtc = s.TimeUtc,
                Value = s.Value,
                Quality = s.Quality,
            });
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return deduped.Count;
    }

    public async Task<IReadOnlyList<ScadaSample>> ListAsync(
        string plantId,
        IReadOnlyCollection<string> signalIds,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentNullException.ThrowIfNull(signalIds);

        var ids = signalIds.ToList();
        var query = _db.SampleFactsFine.AsNoTracking()
            .Where(x => x.AssetId == plantId && x.TimeUtc >= fromUtc && x.TimeUtc < toUtc);
        if (ids.Count > 0)
        {
            query = query.Where(x => ids.Contains(x.SignalId));
        }
        var rows = await query.OrderBy(x => x.TimeUtc).ThenBy(x => x.SignalId)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ConvertAll(ToDomain);
    }

    public async Task<int> DeleteOlderThanAsync(string plantId, DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        return await _db.SampleFactsFine
            .Where(x => x.AssetId == plantId && x.TimeUtc < cutoffUtc)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        return await _db.SampleFactsFine
            .Where(x => x.AssetId == plantId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteAllAsync(CancellationToken ct)
    {
        return await _db.SampleFactsFine.ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static ScadaSample ToDomain(SampleFactFineEntry e) =>
        new(e.AssetId, e.SignalId, e.TimeUtc, e.Value, e.Quality);
}
