using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.Effektivitet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Wrapping query-service for episode-analysen: henter effektivitets-data fra
/// <see cref="IEffectivityQueryService"/>, slår opp time-spotpris fra
/// <c>core.market_prices</c>, og kjører <see cref="IEffektivitetEpisodeService"/>
/// over resultatet. Returner et ferdig DTO klar til UI-en.
///
/// Prisområde-mapping er foreløpig hardkodet til <c>"NO2"</c> for alle Dalane
/// Kraft-anlegg (Rogaland → NO2). På sikt kan det leses fra <c>core.plants</c>
/// hvis vi tar inn anlegg i andre områder.
/// </summary>
public sealed class EffektivitetEpisodeQueryService
{
    private const string DefaultPriceArea = "NO2";

    private readonly IEffectivityQueryService _effektivitet;
    private readonly IEffektivitetEpisodeService _episode;
    private readonly KraftverkDbContext _db;
    private readonly ILogger<EffektivitetEpisodeQueryService> _log;

    public EffektivitetEpisodeQueryService(
        IEffectivityQueryService effektivitet,
        IEffektivitetEpisodeService episode,
        KraftverkDbContext db,
        ILogger<EffektivitetEpisodeQueryService> log)
    {
        _effektivitet = effektivitet ?? throw new ArgumentNullException(nameof(effektivitet));
        _episode = episode ?? throw new ArgumentNullException(nameof(episode));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<EpisodeAnalysisResult> GetAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        EpisodeAnalyseOpsjoner? opsjoner, CancellationToken ct)
    {
        var (effektivitet, priser) = await LoadRawDataAsync(plantId, fromUtc, toUtc, ct)
            .ConfigureAwait(false);
        return _episode.Analyse(effektivitet, priser, opsjoner);
    }

    /// <summary>
    /// Henter rådata (effektivitets-respons + time-spotpriser) for ett anlegg.
    /// Eksponert separat så portefølje-tjenesten kan kjøre analyseren flere
    /// ganger med ulike opsjoner (baseline vs sweet-spot) uten å re-fetche.
    ///
    /// Returnerer null som priser hvis spotpris-tabellen er tom for perioden —
    /// analyseren håndterer det og flagger TaptNokErEstimat.
    /// </summary>
    public async Task<(EffektivitetResponse Effektivitet, IReadOnlyDictionary<DateTimeOffset, double>? Priser)>
        LoadRawDataAsync(string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);

        var effektivitet = await _effektivitet
            .GetAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);

        if (effektivitet.DataMissing || effektivitet.Punkter.Count == 0)
        {
            return (effektivitet, null);
        }

        // Hent spotpriser for time-buckets i perioden.
        var fromHour = new DateTimeOffset(fromUtc.Year, fromUtc.Month, fromUtc.Day,
            fromUtc.Hour, 0, 0, TimeSpan.Zero);
        var toHour = new DateTimeOffset(toUtc.Year, toUtc.Month, toUtc.Day,
            toUtc.Hour, 0, 0, TimeSpan.Zero).AddHours(1);

        var priser = await _db.MarketPrices
            .AsNoTracking()
            .Where(p => p.PriceArea == DefaultPriceArea
                && p.TimeUtc >= fromHour && p.TimeUtc < toHour)
            .ToDictionaryAsync(p => p.TimeUtc, p => p.PriceNokMwh, ct)
            .ConfigureAwait(false);

        _log.LogDebug(
            "LoadRawData for {PlantId}: {Punkter} punkter, {Bins} bins, {Priser} time-spotpriser.",
            plantId, effektivitet.Punkter.Count, effektivitet.Bins.Count, priser.Count);

        return (effektivitet, priser.Count > 0 ? priser : null);
    }
}
