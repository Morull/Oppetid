using System.Text.Json;
using KraftverkUptime.Core.Configuration;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace KraftverkUptime.Infrastructure.Configuration;

/// <summary>
/// Postgres-basert IPlantConfiguration med memory-cache foran.
/// Admin-UI kan invalidere cache via SetAsync. v2 utvides med distribuert cache.
/// </summary>
public sealed class DbPlantConfiguration : IPlantConfiguration
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly KraftverkDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ICurrentUser _user;

    public DbPlantConfiguration(KraftverkDbContext db, IMemoryCache cache, ICurrentUser user)
    {
        _db = db;
        _cache = cache;
        _user = user;
    }

    public async Task<T?> GetAsync<T>(string plantId, string key, CancellationToken ct = default)
    {
        var cacheKey = CacheKey(plantId, key);
        if (_cache.TryGetValue(cacheKey, out T? cached))
        {
            return cached;
        }

        var row = await _db.PlantConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.PlantId == plantId && x.Key == key, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return default;
        }

        var value = JsonSerializer.Deserialize<T>(row.ValueJson);
        _cache.Set(cacheKey, value, CacheTtl);
        return value;
    }

    public async Task SetAsync<T>(string plantId, string key, T value, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(value);
        var row = await _db.PlantConfigurations
            .FirstOrDefaultAsync(x => x.PlantId == plantId && x.Key == key, ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new PlantConfigurationEntry
            {
                OwnerOrgId = _user.OrgId,
                PlantId = plantId,
                Key = key,
                ValueJson = json,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = _user.UserId
            };
            _db.PlantConfigurations.Add(row);
        }
        else
        {
            row.ValueJson = json;
            row.UpdatedAt = DateTimeOffset.UtcNow;
            row.UpdatedBy = _user.UserId;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _cache.Remove(CacheKey(plantId, key));
    }

    private static string CacheKey(string plantId, string key) => $"plantcfg::{plantId}::{key}";
}
