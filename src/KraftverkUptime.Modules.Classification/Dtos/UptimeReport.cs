using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Classification.Dtos;

/// <summary>
/// Output fra SettlementUptimeAnalyzer. Inneholder både de klassifiserte
/// timeradene (for rapport-bygg og UI-drill-down) og den aggregerte
/// KPI-katalogen + state-tellinger.
///
/// Format samsvarer 1:1 med <c>drivdal-feb2025-fasit.json</c> slik at
/// regresjonstester mot fasit er en direkte sammenligning.
/// </summary>
public sealed record UptimeReport
{
    public required string PlantId { get; init; }
    public required DateTimeOffset PeriodStartUtc { get; init; }
    public required DateTimeOffset PeriodEndUtc { get; init; }
    public required int PeriodHours { get; init; }
    public required IReadOnlyDictionary<UnitState, int> StateCounts { get; init; }
    public required IReadOnlyList<ClassifiedHourlyRow> Classified { get; init; }
    public required IReadOnlyList<KpiResult> Kpis { get; init; }
}
