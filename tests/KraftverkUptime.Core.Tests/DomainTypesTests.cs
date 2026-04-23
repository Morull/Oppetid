using FluentAssertions;
using KraftverkUptime.Core.Domain;
using Xunit;

namespace KraftverkUptime.Core.Tests;

public class DomainTypesTests
{
    [Fact]
    public void ClassifiedPeriod_Duration_Is_Positive_For_Normal_Period()
    {
        var from = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddHours(1);

        var period = new ClassifiedPeriod("plantA.unit1", from, to, UnitState.InService,
            "NORMAL", 0.99, new[] { "settlement" }, DataQualityState.Good);

        period.Duration.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void AssetEvent_Is_Record_With_Value_Equality()
    {
        var t = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var e1 = new AssetEvent("a", t, "s", "m", 1.0, DataQualityState.Good, "MW",
            new Dictionary<string, string>(), 1);
        var e2 = e1 with { };

        e1.Should().Be(e2);
    }

    [Fact]
    public void DataQualityState_Has_All_Expected_Values()
    {
        Enum.GetValues<DataQualityState>().Should().Contain(
            [DataQualityState.Good, DataQualityState.Uncertain, DataQualityState.Substituted,
             DataQualityState.InformationUnavailable, DataQualityState.Quarantined, DataQualityState.Rejected]);
    }

    [Fact]
    public void UnitState_Has_All_IEEE762_Aligned_Values()
    {
        Enum.GetValues<UnitState>().Should().Contain(
            [UnitState.InService, UnitState.ReserveShutdown, UnitState.PlannedOutage,
             UnitState.MaintenanceOutage, UnitState.ForcedOutage, UnitState.ForcedDerating,
             UnitState.PlannedDerating, UnitState.ResourceUnavailable, UnitState.InformationUnavailable]);
    }
}
