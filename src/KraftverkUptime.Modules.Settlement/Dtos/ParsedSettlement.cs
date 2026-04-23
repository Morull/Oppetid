using KraftverkUptime.Modules.Settlement.Quality;

namespace KraftverkUptime.Modules.Settlement.Dtos;

/// <summary>
/// Resultatet av å parse en portaleksport-fil. Inneholder både selve dataene
/// (timerader + aggregatrad) og en liste av valideringsavvik som oppsto under
/// parsingen. Manglende timer fylles inn av <see cref="DataQualityReportBuilder"/>
/// i et senere steg, ikke her.
/// </summary>
public sealed record ParsedSettlement
{
    public required string PlantName { get; init; }
    public required string SchemaVersion { get; init; }
    public required DateTimeOffset PeriodStartUtc { get; init; }
    public required DateTimeOffset PeriodEndUtc { get; init; }
    public required IReadOnlyList<SettlementHourlyRow> Hourly { get; init; }
    public SettlementSummaryRow? Summary { get; init; }
    public required IReadOnlyList<ValidationIssue> Issues { get; init; }
}
