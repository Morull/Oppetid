using System.Diagnostics;
using KraftverkUptime.Core.Analysis;
using KraftverkUptime.Core.Events;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Reporting;
using KraftverkUptime.Modules.Settlement.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Events;

/// <summary>
/// Konsumerer <see cref="SettlementImportedEvent"/> og UPSERT-er spotpris-rader
/// fra settlement-fila til <c>core.market_prices</c>. Hver rad blir
/// (PriceArea, TimeUtc, PriceNokMwh, source="settlement") slik at capture-rate-
/// kalkulatoren kan hente pris-baseline uten å re-parse fila.
///
/// PriceArea hentes fra <c>Plant.PriceArea</c> (default NO2). Rader med null
/// SpotprisNokMwh hoppes over.
///
/// Idempotent: samme fil opp-lastet to ganger → samme upsert. ON CONFLICT-
/// håndtering via EF + composite-key (PriceArea, TimeUtc): vi sletter
/// eksisterende rader for nøkkelsettet og setter inn på nytt med fersk
/// recorded_at_utc. "Settlement"-source vinner alltid over "entsoe" siden
/// vi alltid overskriver ved upsert.
/// </summary>
public sealed class MarketPriceUpsertHandler : IEventHandler<SettlementImportedEvent>
{
    private readonly IUptimePeriodProvider _periodProvider;
    private readonly KraftverkDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<MarketPriceUpsertHandler> _logger;

    public MarketPriceUpsertHandler(
        IUptimePeriodProvider periodProvider,
        KraftverkDbContext db,
        TimeProvider clock,
        ILogger<MarketPriceUpsertHandler> logger)
    {
        _periodProvider = periodProvider ?? throw new ArgumentNullException(nameof(periodProvider));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task HandleAsync(SettlementImportedEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var sw = Stopwatch.StartNew();

        var plant = await _db.Plants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == domainEvent.PlantId, ct).ConfigureAwait(false);
        if (plant is null)
        {
            _logger.LogWarning(
                "MarketPriceUpsert: plant {PlantId} eksisterer ikke. Hopper over.",
                domainEvent.PlantId);
            return;
        }
        var priceArea = string.IsNullOrWhiteSpace(plant.PriceArea) ? "NO2" : plant.PriceArea;

        var period = await _periodProvider
            .GetAsync(domainEvent.PlantId, domainEvent.PeriodStartUtc, domainEvent.PeriodEndUtc, ct)
            .ConfigureAwait(false);

        // Bygg unike (TimeUtc, Spotpris)-rader. Settlement har samme spotpris
        // for hele NO2 — flere anlegg som importerer samme måned ender opp
        // med samme rader, og vi upserter alle.
        var prices = new Dictionary<DateTimeOffset, double>();
        foreach (var row in period.Settlement.Hourly)
        {
            if (!row.SpotprisNokMwh.HasValue) continue;
            // Siste rad vinner ved duplikat (samme time)
            prices[row.TimeUtc] = row.SpotprisNokMwh.Value;
        }
        if (prices.Count == 0)
        {
            _logger.LogInformation(
                "MarketPriceUpsert: ingen pris-rader fra plant {PlantId} (alle null).",
                domainEvent.PlantId);
            return;
        }

        // Slett eksisterende rader for (priceArea, time-set) før insert.
        // EF tracker forhindrer parallell-insert med samme key.
        var times = prices.Keys.ToList();
        var minTime = times.Min();
        var maxTime = times.Max();
        var existing = await _db.MarketPrices
            .Where(x => x.PriceArea == priceArea && x.TimeUtc >= minTime && x.TimeUtc <= maxTime)
            .ToListAsync(ct).ConfigureAwait(false);

        var existingTimes = existing.Select(e => e.TimeUtc).ToHashSet();
        var toRemove = existing.Where(e => prices.ContainsKey(e.TimeUtc)).ToList();
        if (toRemove.Count > 0)
        {
            _db.MarketPrices.RemoveRange(toRemove);
        }

        var now = _clock.GetUtcNow();
        foreach (var (time, price) in prices)
        {
            _db.MarketPrices.Add(new MarketPriceEntry
            {
                PriceArea = priceArea,
                TimeUtc = time,
                PriceNokMwh = price,
                Source = "settlement",
                RecordedAtUtc = now,
            });
        }
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation(
            "MarketPriceUpsert: {PlantId} → {Count} rader upserted i {Area} på {Ms} ms.",
            domainEvent.PlantId, prices.Count, priceArea, sw.ElapsedMilliseconds);
    }
}
