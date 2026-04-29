using KraftverkUptime.Core.Storage;
using KraftverkUptime.Infrastructure.Configuration;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting;
using KraftverkUptime.Modules.Settlement;
using KraftverkUptime.Modules.Settlement.Dtos;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Default <see cref="IUptimePeriodProvider"/>. Finner siste import som dekker
/// forespurt periode, henter Excel-blobben fra <see cref="IFileStorage"/>,
/// re-parser via <see cref="ISettlementParser"/>, og bygger
/// <see cref="PlantClassificationConfig"/> via
/// <see cref="PlantClassificationConfigProvider"/>.
///
/// Det ferdig-parsede <c>ParsedSettlement</c>-objektet caches i minne (30 min
/// TTL) per (plantId, idempotencyKey) for å unngå repeat-parse ved gjentatte
/// rapport-forespørsler. Dette gir ~500 ms p50-kostnad ved cache-miss,
/// sub-millisekund ved cache-hit.
/// </summary>
public sealed class SettlementUptimePeriodProvider : IUptimePeriodProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private readonly ISettlementImportRecorder _recorder;
    private readonly IFileStorage _fileStorage;
    private readonly ISettlementParser _parser;
    private readonly PlantClassificationConfigProvider _configProvider;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SettlementUptimePeriodProvider> _logger;

    public SettlementUptimePeriodProvider(
        ISettlementImportRecorder recorder,
        IFileStorage fileStorage,
        ISettlementParser parser,
        PlantClassificationConfigProvider configProvider,
        IMemoryCache cache,
        ILogger<SettlementUptimePeriodProvider> logger)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<UptimePeriod> GetAsync(
        string assetId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(assetId))
        {
            throw new ArgumentException("assetId mangler.", nameof(assetId));
        }
        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("toUtc må være etter fromUtc.", nameof(toUtc));
        }

        var import = await _recorder
            .FindLatestCoveringAsync(assetId, fromUtc, toUtc, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Ingen settlement-import dekker perioden {fromUtc:o} – {toUtc:o} for plant '{assetId}'.");

        var parsed = await GetOrParseAsync(import, ct).ConfigureAwait(false);
        var config = await _configProvider.GetAsync(assetId, ct).ConfigureAwait(false);

        return new UptimePeriod(parsed, config);
    }

    private async Task<ParsedSettlement> GetOrParseAsync(SettlementImportRecord import, CancellationToken ct)
    {
        var cacheKey = CacheKey(import);

        if (_cache.TryGetValue(cacheKey, out ParsedSettlement? cached) && cached is not null)
        {
            return cached;
        }

        _logger.LogDebug(
            "ParsedSettlement cache-miss for {CacheKey}; re-parser blob {Blob}",
            cacheKey, import.BlobPath);

        await using var stream = await _fileStorage.GetAsync(import.BlobPath, ct).ConfigureAwait(false);

        // Parser kan returnere flere ParsedSettlement i samme workbook (multi-
        // plant-format). Vi finner riktig anlegg ved å matche PlantId mot
        // import-raden — dekkes både gammelt enkelt-plant-format (PlantId=null
        // i parsed → first-and-only) og nytt multi-plant-format.
        var all = await _parser.ParseAllAsync(stream, ct).ConfigureAwait(false);
        var parsed = all.FirstOrDefault(p => string.Equals(p.PlantId, import.PlantId, StringComparison.Ordinal))
            ?? (all.Count == 1 ? all[0] : null)
            ?? throw new InvalidOperationException(
                $"Ingen ParsedSettlement i blob {import.BlobPath} matcher plantId '{import.PlantId}'.");

        _cache.Set(cacheKey, parsed, CacheTtl);
        return parsed;
    }

    private static string CacheKey(SettlementImportRecord import)
        => $"settlement::{import.PlantId}::{import.IdempotencyKey}";
}
