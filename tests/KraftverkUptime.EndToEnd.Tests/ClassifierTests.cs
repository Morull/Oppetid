using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Classification;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Settlement.Dtos;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Regel-for-regel-tester for SettlementClassifier. Hver test isolerer én
/// regel slik at feilmeldinger peker til én konkret regel når de bryter.
/// </summary>
public class ClassifierTests
{
    private static readonly PlantClassificationConfig Regulated = new()
    {
        PlantId = "Test",
        PlantType = PlantType.Regulated,
        NominalPowerMw = 2.2,
    };

    private static readonly PlantClassificationConfig RunOfRiver = new()
    {
        PlantId = "TestROR",
        PlantType = PlantType.RunOfRiver,
        NominalPowerMw = 2.2,
    };

    [Fact]
    public void Row_WithMissingElhub_IsInformationUnavailable()
    {
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: null, plan: 2.0, spot: 500, dq: DataQualityState.InformationUnavailable),
        }, Regulated);

        classified[0].State.Should().Be(UnitState.InformationUnavailable);
        classified[0].Confidence.Should().Be(1.0);
    }

    [Fact]
    public void Row_WithZeroElhub_AndPositivePlan_IsForcedOutage()
    {
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: 0, plan: 2.0, spot: 500),
        }, Regulated);

        classified[0].State.Should().Be(UnitState.ForcedOutage);
        classified[0].CauseCode.Should().Be("U1-UnplannedStop");
    }

    [Fact]
    public void Row_WithZeroElhub_AndZeroPlan_AndSustainedRun_IsPlannedOutage()
    {
        // 25 sammenhengende null-timer → PO
        var hours = new List<SettlementHourlyRow>();
        for (int i = 0; i < 25; i++)
        {
            hours.Add(Hour(i, mwhElhub: 0, plan: 0, spot: 500));
        }
        var classified = Classify(hours, Regulated);

        classified.Should().AllSatisfy(c =>
            c.State.Should().Be(UnitState.PlannedOutage));
    }

    [Fact]
    public void Row_WithZeroElhub_AndZeroPlan_AndLowSpot_IsReserveShutdown()
    {
        // Median = 500 i data; test-time har spot = 200 < median
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: 0, plan: 0, spot: 200),
            Hour(1, mwhElhub: 1.9, plan: 2.0, spot: 500),
            Hour(2, mwhElhub: 1.9, plan: 2.0, spot: 800),
        }, Regulated);

        classified[0].State.Should().Be(UnitState.ReserveShutdown);
        classified[0].CauseCode.Should().Be("M1-MarketDriven");
    }

    [Fact]
    public void Row_WithPositiveElhub_AndPositivePlan_RatioBelow90Pct_IsForcedDerating()
    {
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: 1.5, plan: 2.0, spot: 500),  // 75 % = under 90 %
        }, Regulated);

        classified[0].State.Should().Be(UnitState.ForcedDerating);
        classified[0].CauseCode.Should().Be("D1-ForcedDerating");
    }

    [Fact]
    public void Row_WithPositiveElhub_AndRatioAbove90Pct_IsInService()
    {
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: 1.95, plan: 2.0, spot: 500),  // 97,5 %
        }, Regulated);

        classified[0].State.Should().Be(UnitState.InService);
        classified[0].Confidence.Should().Be(0.95);
    }

    [Fact]
    public void RunOfRiver_WithZeroElhub_IsResourceUnavailable()
    {
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: 0, plan: 0, spot: 500),
        }, RunOfRiver);

        classified[0].State.Should().Be(UnitState.ResourceUnavailable);
    }

    // ------------------------------------------------------------------
    private static IReadOnlyList<ClassifiedHourlyRow> Classify(
        IReadOnlyList<SettlementHourlyRow> hours, PlantClassificationConfig plant)
    {
        var classifier = new SettlementClassifier();
        return classifier.Classify(hours, plant);
    }

    private static SettlementHourlyRow Hour(
        int offsetHours,
        double? mwhElhub,
        double? plan,
        double? spot,
        DataQualityState dq = DataQualityState.Good)
    {
        var baseTime = new DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var utc = baseTime.AddHours(offsetHours);
        return new SettlementHourlyRow
        {
            TimeUtc = utc,
            TimeLocal = utc.ToOffset(TimeSpan.FromHours(1)),
            MwhElhub = mwhElhub,
            MwhESett = mwhElhub,
            ProduksjonplanMwh = plan,
            SpotprisNokMwh = spot,
            DqState = dq,
        };
    }
}
