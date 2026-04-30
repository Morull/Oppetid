namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// Day-ahead spotpris per (prisområde, time). Persistert i <c>core.market_prices</c>.
///
/// Kilden (<see cref="Source"/>) er typisk <c>"settlement"</c> (utdraget fra
/// settlement-fila ved import) eller <c>"entsoe"</c> (backfill for hull).
/// Settlement har høyere prioritet siden det er kontraktuell pris;
/// ENTSO-E brukes når settlement-fila ikke dekker timen.
///
/// Brukes som baseline for capture rate (volumvektet snittpris vs anleggets
/// oppnådde pris) og for vakt-ROI-tap-beregninger.
/// </summary>
public sealed class MarketPriceEntry
{
    public required string PriceArea { get; set; }   // "NO2", "NO5" osv.
    public required DateTimeOffset TimeUtc { get; set; }
    public required double PriceNokMwh { get; set; }
    public required string Source { get; set; }      // "settlement", "entsoe", "manual_csv"
    public DateTimeOffset RecordedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
