using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF-implementasjon av <see cref="IScadaSampleRepository"/>. Bulk-INSERT
/// bruker AddRange + SaveChangesAsync foreløpig — bra nok for time-aggregat
/// (~28k rader/mnd/anlegg). Kan byttes til Npgsql.BeginBinaryImportAsync
/// (COPY-protokoll) når SCADA-frekvens går opp og INSERT blir flaskehals.
/// </summary>
public sealed class EfScadaSampleRepository : IScadaSampleRepository
{
    private readonly KraftverkDbContext _db;

    public EfScadaSampleRepository(KraftverkDbContext db)
    {
        _db = db;
    }

    public async Task<int> BulkInsertAsync(IReadOnlyCollection<ScadaSample> samples, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) return 0;

        // Idempotens via upsert: vi sletter eksisterende rader for de
        // (asset_id, signal_id, time_utc)-kombinasjonene vi skal skrive,
        // og setter dem inn på nytt. Holder API-kontrakten enkel.
        var assetIds = samples.Select(s => s.AssetId).Distinct().ToList();
        var minTime = samples.Min(s => s.TimeUtc);
        var maxTime = samples.Max(s => s.TimeUtc);

        var existing = await _db.SampleFacts
            .Where(x => assetIds.Contains(x.AssetId) && x.TimeUtc >= minTime && x.TimeUtc <= maxTime)
            .ToListAsync(ct).ConfigureAwait(false);

        var keys = samples.Select(s => (s.AssetId, s.SignalId, s.TimeUtc)).ToHashSet();
        var toRemove = existing.Where(e => keys.Contains((e.AssetId, e.SignalId, e.TimeUtc))).ToList();
        if (toRemove.Count > 0)
        {
            _db.SampleFacts.RemoveRange(toRemove);
        }

        foreach (var s in samples)
        {
            _db.SampleFacts.Add(new SampleFactEntry
            {
                AssetId = s.AssetId,
                SignalId = s.SignalId,
                TimeUtc = s.TimeUtc,
                Value = s.Value,
                Quality = s.Quality,
            });
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return samples.Count;
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
        var query = _db.SampleFacts.AsNoTracking()
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
        return await _db.SampleFacts
            .Where(x => x.AssetId == plantId && x.TimeUtc < cutoffUtc)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        return await _db.SampleFacts
            .Where(x => x.AssetId == plantId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> DeleteAllAsync(CancellationToken ct)
    {
        return await _db.SampleFacts.ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static ScadaSample ToDomain(SampleFactEntry e) =>
        new(e.AssetId, e.SignalId, e.TimeUtc, e.Value, e.Quality);
}
