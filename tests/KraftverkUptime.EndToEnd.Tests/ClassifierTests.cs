using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Classification;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Settlement.Dtos;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Regel-for-regel-tester for SettlementClassifier.
///
/// Modellen produserer disse tilstandene automatisk:
/// <list type="bullet">
///   <item>InService — Elhub > 0 (eller negativ Elhub for Pumped = pumping)</item>
///   <item>ForcedOutage — Elhub = 0 og Spotbud > 0</item>
///   <item>ReserveShutdown — Elhub = 0, Spotbud = 0/null, og PlantType ≠ RunOfRiver</item>
///   <item>ResourceUnavailable — Elhub = 0, Spotbud = 0/null, og PlantType = RunOfRiver
///         (vannmangel-heuristikk for elvekraft, SPEC-MVP-HARDENING D)</item>
///   <item>ForcedDerating — Elhub > 0 men under DeratingThreshold × Plan</item>
///   <item>InformationUnavailable — Elhub mangler eller negativ (untatt Pumped)</item>
/// </list>
///
/// PlannedOutage / MaintenanceOutage produseres ikke automatisk — de kommer
/// fra manuell annotering.
/// </summary>
public class ClassifierTests
{
    private static readonly PlantClassificationConfig DefaultPlant = new()
    {
        PlantId = "Test",
        PlantType = PlantType.Regulated,
        NominalPowerMw = 2.2,
    };

    private static PlantClassificationConfig PlantOf(PlantType type) => new()
    {
        PlantId = "Test-" + type,
        PlantType = type,
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

    // ===========================================================================
    // SPEC-MVP-HARDENING tiltak D: PlantType-forgrening
    // ===========================================================================

    [Fact(DisplayName = "RunOfRiver: 0/0-time klassifiseres som ResourceUnavailable (vannmangel)")]
    public void RunOfRiver_NoProductionNoBid_ClassifiesAsResourceUnavailable()
    {
        var classified = ClassifyAs(PlantType.RunOfRiver, new[]
        {
            Hour(0, mwhElhub: 0, bid: 0),
            Hour(1, mwhElhub: 0, bid: null),
        });

        classified[0].State.Should().Be(UnitState.ResourceUnavailable);
        classified[0].CauseCode.Should().Be("R1-LowInflow");
        classified[0].Confidence.Should().Be(0.80);
        classified[0].Rationale.Should().Contain("elvekraft");

        classified[1].State.Should().Be(UnitState.ResourceUnavailable);
    }

    [Fact(DisplayName = "RunOfRiver: Elhub > 0 → InService (uendret)")]
    public void RunOfRiver_PositiveElhub_StillInService()
    {
        var classified = ClassifyAs(PlantType.RunOfRiver, new[]
        {
            Hour(0, mwhElhub: 1.5, bid: 0),
        });

        classified[0].State.Should().Be(UnitState.InService);
    }

    [Fact(DisplayName = "RunOfRiver: 0/Spotbud > 0 → ForcedOutage (uendret)")]
    public void RunOfRiver_ZeroElhubWithBid_StillForcedOutage()
    {
        // Selv elvekraft er forpliktet til levering hvis det er bud — at vannet
        // sviktet er ikke en gyldig unnskyldning kontraktsmessig.
        var classified = ClassifyAs(PlantType.RunOfRiver, new[]
        {
            Hour(0, mwhElhub: 0, bid: 1.5),
        });

        classified[0].State.Should().Be(UnitState.ForcedOutage);
    }

    [Fact(DisplayName = "Regulated: 0/0-time klassifiseres som ReserveShutdown")]
    public void Regulated_NoProductionNoBid_ClassifiesAsReserveShutdown()
    {
        var classified = ClassifyAs(PlantType.Regulated, new[]
        {
            Hour(0, mwhElhub: 0, bid: 0),
        });

        classified[0].State.Should().Be(UnitState.ReserveShutdown);
        classified[0].CauseCode.Should().Be("M1-NoCommitment");
    }

    [Fact(DisplayName = "Mixed: 0/0-time klassifiseres som ReserveShutdown (samme som Regulated)")]
    public void Mixed_NoProductionNoBid_ClassifiesAsReserveShutdown()
    {
        // Mixed (lite magasin) er pragmatisk = Regulated. Drifts-leder kan
        // velge å stå stille, så det er ikke automatisk ResourceUnavailable.
        var classified = ClassifyAs(PlantType.Mixed, new[]
        {
            Hour(0, mwhElhub: 0, bid: 0),
        });

        classified[0].State.Should().Be(UnitState.ReserveShutdown);
    }

    [Fact(DisplayName = "Pumped: negativ Elhub klassifiseres som InService (pumping)")]
    public void Pumped_NegativeElhub_ClassifiesAsInService()
    {
        // For pumpekraft er negativ MWh normal drift (verket bruker strøm
        // for å løfte vann). Skal ikke flagges som datafeil.
        var classified = ClassifyAs(PlantType.Pumped, new[]
        {
            Hour(0, mwhElhub: -2.5, bid: 0),
        });

        classified[0].State.Should().Be(UnitState.InService);
        classified[0].CauseCode.Should().Be("P1-Pumping");
        classified[0].Rationale.Should().Contain("Pumpedrift");
    }

    [Fact(DisplayName = "Regulated: negativ Elhub klassifiseres som InformationUnavailable")]
    public void Regulated_NegativeElhub_ClassifiesAsInformationUnavailable()
    {
        // For magasinverk er negativ MWh enten datafeil eller regulerkraft-
        // kjøp; vi har ikke nok info til å skille → InformationUnavailable.
        var classified = ClassifyAs(PlantType.Regulated, new[]
        {
            Hour(0, mwhElhub: -2.5, bid: 0),
        });

        classified[0].State.Should().Be(UnitState.InformationUnavailable);
        classified[0].CauseCode.Should().Be("9.2-NegativeReading");
    }

    [Fact(DisplayName = "RunOfRiver: negativ Elhub klassifiseres som InformationUnavailable (samme som Regulated)")]
    public void RunOfRiver_NegativeElhub_ClassifiesAsInformationUnavailable()
    {
        // Elvekraft har ikke pumpe-funksjon — negativ Elhub er datafeil.
        var classified = ClassifyAs(PlantType.RunOfRiver, new[]
        {
            Hour(0, mwhElhub: -1.0, bid: 0),
        });

        classified[0].State.Should().Be(UnitState.InformationUnavailable);
    }

    // ------------------------------------------------------------------
    private static IReadOnlyList<ClassifiedHourlyRow> Classify(
        IReadOnlyList<SettlementHourlyRow> hours)
    {
        var classifier = new SettlementClassifier();
        return classifier.Classify(hours, DefaultPlant);
    }

    private static IReadOnlyList<ClassifiedHourlyRow> ClassifyAs(
        PlantType type, IReadOnlyList<SettlementHourlyRow> hours)
    {
        var classifier = new SettlementClassifier();
        return classifier.Classify(hours, PlantOf(type));
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
