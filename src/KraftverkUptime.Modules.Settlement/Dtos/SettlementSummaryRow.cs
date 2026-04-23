namespace KraftverkUptime.Modules.Settlement.Dtos;

/// <summary>
/// Aggregatrad fra Summering-fanen. Brukes primært for kryssvalidering mot
/// summen av timerader. Avvik &gt; 0,1 % flagges som <c>SUMMARY_HOURLY_MISMATCH</c>.
/// </summary>
public sealed record SettlementSummaryRow
{
    public required string TimeseriesLabel { get; init; }

    public double? MwhElhub { get; init; }
    public double? MwhESett { get; init; }
    public double? SpotbudMwh { get; init; }
    public double? SpotomsetningNok { get; init; }
    public double? UbalanseMwh { get; init; }
    public double? RkKjopNok { get; init; }
    public double? RkSalgNok { get; init; }
    public double? NordPoolGebyrNok { get; init; }
    public double? ESettVolumgebyrNok { get; init; }
    public double? ESettUbalansegebyrNok { get; init; }
    public double? SumSalgNok { get; init; }
    public double? MeglerprovisjonNok { get; init; }
    public double? OppgjorNok { get; init; }
}
