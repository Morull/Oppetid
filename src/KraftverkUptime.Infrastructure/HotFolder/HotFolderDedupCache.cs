using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.HotFolder;

/// <summary>
/// Innholds-hash-basert duplikat-cache for hot-folder. Forhindrer at samme fil
/// blir prosessert flere ganger hvis brukeren dropper den på nytt eller trykker
/// "Skann nå" gjentatte ganger.
///
/// DB-laget er allerede idempotent på (file_hash, plant_id), men det blir
/// likevel støy i historikk og kø-buffer hvis duplikatet får lov til å gå
/// gjennom hele import-pipelinen. Denne cachen kortslutter før importør-kallet.
///
/// Persistering: JSON-fil i hot-folder-roten (<c>.hotfolder-dedup.json</c>).
/// Overlever container-restart. Auto-prunes etter <see cref="HotFolderOptions.DedupRetentionDays"/>.
///
/// Hash-strategi: SHA-256 av råinnhold. Følger samme konvensjon som
/// <c>SettlementsEndpoints</c>/<c>MultiPlantSettlementsEndpoints</c>.
/// </summary>
public sealed class HotFolderDedupCache
{
    private readonly string _cachePath;
    private readonly TimeSpan _retention;
    private readonly ILogger<HotFolderDedupCache> _log;
    private readonly object _lock = new();
    private Dictionary<string, DedupRecord> _byHash;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public HotFolderDedupCache(HotFolderOptions options, ILogger<HotFolderDedupCache> log)
    {
        _cachePath = Path.Combine(options.RootPath, options.DedupCacheFileName);
        _retention = TimeSpan.FromDays(Math.Max(1, options.DedupRetentionDays));
        _log = log;
        _byHash = LoadFromDisk();
    }

    /// <summary>Beregn SHA-256 av filinnhold (lowercase hex).</summary>
    public static async Task<string> ComputeFileHashAsync(FileInfo file, CancellationToken ct = default)
    {
        await using var stream = file.OpenRead();
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Forsøker å registrere en ny hash. Returnerer false hvis hashen allerede
    /// er sett innenfor retention-vinduet — i så fall settes <paramref name="existing"/>.
    /// Ved registrering persisteres cachen til disk umiddelbart.
    /// </summary>
    public bool TryRegister(string fileHash, string fileName, string? plantId, string? sourceType,
        out DedupRecord existing)
    {
        lock (_lock)
        {
            PruneExpired();
            if (_byHash.TryGetValue(fileHash, out var prev))
            {
                existing = prev;
                return false;
            }
            var record = new DedupRecord(
                Hash: fileHash,
                FirstFileName: fileName,
                FirstSeenUtc: DateTimeOffset.UtcNow,
                PlantId: plantId,
                SourceType: sourceType);
            _byHash[fileHash] = record;
            existing = record;
            SaveToDisk();
            return true;
        }
    }

    /// <summary>Slett alle records — brukes av tester.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _byHash.Clear();
            SaveToDisk();
        }
    }

    /// <summary>Gjeldende cache-størrelse (etter prune).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                PruneExpired();
                return _byHash.Count;
            }
        }
    }

    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - _retention;
        var expired = _byHash.Values.Where(r => r.FirstSeenUtc < cutoff).ToList();
        if (expired.Count == 0) return;
        foreach (var r in expired)
        {
            _byHash.Remove(r.Hash);
        }
        _log.LogDebug("Dedup-cache: pruned {Count} entries older than {Days} days.",
            expired.Count, _retention.TotalDays);
    }

    private Dictionary<string, DedupRecord> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_cachePath)) return new();
            var json = File.ReadAllText(_cachePath);
            var records = JsonSerializer.Deserialize<List<DedupRecord>>(json, JsonOpts) ?? new();
            var dict = records.ToDictionary(r => r.Hash, r => r);
            _log.LogInformation("Dedup-cache lastet: {Count} records fra {Path}.", dict.Count, _cachePath);
            return dict;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Klarte ikke laste dedup-cache fra {Path} — starter tom.", _cachePath);
            return new();
        }
    }

    private void SaveToDisk()
    {
        try
        {
            var json = JsonSerializer.Serialize(_byHash.Values.ToList(), JsonOpts);
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Klarte ikke skrive dedup-cache til {Path}.", _cachePath);
        }
    }
}

/// <summary>
/// Én rad i dedup-cachen. <see cref="FirstFileName"/> er filnavnet vi så
/// første gang — neste gang samme innhold dukker opp kan filnavnet være
/// annerledes (eks. brukeren har lagret en kopi).
/// </summary>
public sealed record DedupRecord(
    string Hash,
    string FirstFileName,
    DateTimeOffset FirstSeenUtc,
    string? PlantId,
    string? SourceType);
