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
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);

        var effektivitet = await _effektivitet
            .GetAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);

        if (effektivitet.DataMissing || effektivitet.Punkter.Count == 0)
        {
            // Ingen data → ingen episoder. Returner tomt-resultat med
            // ManglerSpotpriser=true så UI kan vise rett melding.
            return _episode.Analyse(effektivitet, spotPrisNokMwhPerTime: null, opsjoner);
        }

        // Hent spotpriser for time-buckets i perioden. ToHourBucket-logikken er
        // duplisert inne i analyseren — sørger her bare for at vi henter litt
        // bredt nok så ingen "manglende pris"-flagg slår inn pga grense-tilfeller.
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
            "Episode-analyse for {PlantId}: {Punkter} punkter, {Bins} bins, {Priser} time-spotpriser.",
            plantId, effektivitet.Punkter.Count, effektivitet.Bins.Count, priser.Count);

        return _episode.Analyse(
            effektivitet,
            spotPrisNokMwhPerTime: priser.Count > 0 ? priser : null,
            opsjoner);
    }
}
