using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Classification;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Settlement.Dtos;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Regel-for-regel-tester for forenklet SettlementClassifier (post-Phase A).
///
/// Modellen produserer kun fire tilstander automatisk:
/// <list type="bullet">
///   <item>InService — Elhub > 0</item>
///   <item>ForcedOutage — Elhub = 0 og Spotbud > 0</item>
///   <item>ReserveShutdown — Elhub = 0 og Spotbud = 0/null</item>
///   <item>InformationUnavailable — Elhub mangler eller er negativ</item>
/// </list>
///
/// Tilstandene PlannedOutage, MaintenanceOutage, ResourceUnavailable og
/// ForcedDerating produseres ikke automatisk lenger – de skal komme via
/// manuell annotering når den funksjonen er bygd.
/// </summary>
public class ClassifierTests
{
    private static readonly PlantClassificationConfig DefaultPlant = new()
    {
        PlantId = "Test",
        PlantType = PlantType.Regulated, // Type påvirker ikke logikken lenger
        NominalPowerMw = 2.2,
    };

    [Fact]
    public void Row_WithMissingElhub_IsInformationUnavailable()
    {
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: null, bid: 2.0, dq: DataQualityState.InformationUnavailable),
        });

        classified[0].State.Should().Be(UnitState.InformationUnavailable);
        classified[0].CauseCode.Should().Be("9.1-DataMissing");
        classified[0].Confidence.Should().Be(1.0);
    }

    [Fact]
    public void Row_WithNegativeElhub_IsInformationUnavailable()
    {
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: -0.5, bid: 2.0),
        });

        classified[0].State.Should().Be(UnitState.InformationUnavailable);
        classified[0].CauseCode.Should().Be("9.2-NegativeReading");
    }

    [Fact]
    public void Row_WithPositiveElhub_IsInService_RegardlessOfBidOrPlan()
    {
        // Elhub > 0 → InService, uavhengig av bud, plan eller spotpris.
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: 1.5, bid: 2.0),    // under bud, men kjører
            Hour(1, mwhElhub: 0.05, bid: 2.0),   // mye under bud, men kjører
            Hour(2, mwhElhub: 2.5, bid: 0),      // over bud, kjører
            Hour(3, mwhElhub: 1.0, bid: null),   // ingen bud-info
        });

        classified.Should().AllSatisfy(c =>
        {
            c.State.Should().Be(UnitState.InService);
            c.CauseCode.Should().Be("0-Normal");
            c.Confidence.Should().Be(0.95);
        });
    }

    [Fact]
    public void Row_WithZeroElhub_AndPositiveBid_IsForcedOutage()
    {
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: 0, bid: 2.0),
        });

        classified[0].State.Should().Be(UnitState.ForcedOutage);
        classified[0].CauseCode.Should().Be("U1-UnplannedStop");
        classified[0].Confidence.Should().Be(0.90);
    }

    [Fact]
    public void Row_WithZeroElhub_AndZeroBid_IsReserveShutdown()
    {
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: 0, bid: 0),
        });

        classified[0].State.Should().Be(UnitState.ReserveShutdown);
        classified[0].CauseCode.Should().Be("M1-NoCommitment");
        classified[0].Confidence.Should().Be(0.80);
    }

    [Fact]
    public void Row_WithZeroElhub_AndNullBid_IsReserveShutdown()
    {
        var classified = Classify(new[]
        {
            Hour(0, mwhElhub: 0, bid: null),
        });

        classified[0].State.Should().Be(UnitState.ReserveShutdown);
        classified[0].CauseCode.Should().Be("M1-NoCommitment");
    }

    [Fact]
    public void Empty_Hourly_ReturnsEmpty()
    {
        var classified = Classify(Array.Empty<SettlementHourlyRow>());
        classified.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    private static IReadOnlyList<ClassifiedHourlyRow> Classify(
        IReadOnlyList<SettlementHourlyRow> hours)
    {
        var classifier = new SettlementClassifier();
        return classifier.Classify(hours, DefaultPlant);
    }

    private static SettlementHourlyRow Hour(
        int offsetHours,
        double? mwhElhub,
        double? bid,
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
            SpotbudMwh = bid,
            DqState = dq,
        };
    }
}
