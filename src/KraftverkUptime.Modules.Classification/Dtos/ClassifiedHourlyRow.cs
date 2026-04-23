using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Settlement.Dtos;

namespace KraftverkUptime.Modules.Classification.Dtos;

/// <summary>
/// En settlement-time beriket med klassifisering. Beholder hele
/// <see cref="SettlementHourlyRow"/> slik at KPI-beregning og rapport-builder
/// kan bruke både rådata og klassifisering uten å slå sammen lister.
/// </summary>
public sealed record ClassifiedHourlyRow
{
    public required SettlementHourlyRow Row { get; init; }
    public required UnitState State { get; init; }
    public required string CauseCode { get; init; }
    public required double Confidence { get; init; }
    public required string Rationale { get; init; }

    // Proxy-felter for raskere LINQ i KPI-laget
    public DateTimeOffset TimeUtc => Row.TimeUtc;
    public double? MwhElhub => Row.MwhElhub;
    public double? ProduksjonplanMwh => Row.ProduksjonplanMwh;
    public double? SpotprisNokMwh => Row.SpotprisNokMwh;
}
