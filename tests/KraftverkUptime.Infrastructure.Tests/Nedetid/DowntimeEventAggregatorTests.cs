using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.Nedetid;
using KraftverkUptime.Modules.Settlement.Dtos;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Nedetid;

/// <summary>
/// Pure-function-tester for event-aggregeringen. Bygger syntetiske
/// ClassifiedHourlyRow-sekvenser og verifiserer:
///   – Sammenhengende nedetidstimer slås sammen til ett event
///   – Hull i timene avslutter event
///   – State-bytte starter et nytt event
///   – Tap_mwh og tap_nok summeres riktig over alle timene
///   – Operlog-overlay setter HarOperlogMatch + overstyrer cause
/// </summary>
public class DowntimeEventAggregatorTests
{
    [Fact]
    public void Tom_Liste_Returnerer_Tom_Resultat()
    {
        var events = DowntimeEventAggregator.Aggregate("drivdal", Array.Empty<ClassifiedHourlyRow>());
        events.Should().BeEmpty();
    }

    [Fact]
    public void Tre_Sammenhengende_FO_Timer_Blir_Ett_Event()
    {
        var hours = new[]
        {
            HourRow(0,  UnitState.InService,    plan: 1.0, spot: 500),
            HourRow(1,  UnitState.ForcedOutage, plan: 1.5, spot: 600),
            HourRow(2,  UnitState.ForcedOutage, plan: 1.5, spot: 600),
            HourRow(3,  UnitState.ForcedOutage, plan: 1.0, spot: 700),
            HourRow(4,  UnitState.InService,    plan: 1.0, spot: 500),
        };

        var events = DowntimeEventAggregator.Aggregate("drivdal", hours);

        events.Should().HaveCount(1);
        var e = events[0];
        e.StartUtc.Should().Be(BaseUtc.AddHours(1));
        e.EndUtc.Should().Be(BaseUtc.AddHours(4));
        e.VarighetTimer.Should().Be(3);
        e.State.Should().Be(UnitState.ForcedOutage);
        e.Category.Should().Be(DowntimeEventCategory.TripFeil);
        e.TimerSettlement.Should().Be(3);
        e.TapMwh.Should().Be(1.5 + 1.5 + 1.0);
        e.TapNok.Should().Be((1.5 * 600) + (1.5 * 600) + (1.0 * 700));
    }

    [Fact]
    public void Hull_Mellom_Nedetidstimer_Avslutter_Event_Og_Starter_Nytt()
    {
        // Time 0=FO, 1=mangler (skip), 2=FO → to events
        var hours = new[]
        {
            HourRow(0, UnitState.ForcedOutage, plan: 1.0, spot: 500),
            // Time 1 mangler i listen
            HourRow(2, UnitState.ForcedOutage, plan: 1.0, spot: 500),
        };

        var events = DowntimeEventAggregator.Aggregate("drivdal", hours);

        events.Should().HaveCount(2);
        events[0].EndUtc.Should().Be(BaseUtc.AddHours(1));
        events[1].StartUtc.Should().Be(BaseUtc.AddHours(2));
    }

    [Fact]
    public void Bytte_Mellom_FO_Og_MO_Starter_Nytt_Event()
    {
        var hours = new[]
        {
            HourRow(0, UnitState.ForcedOutage,    plan: 1.0, spot: 500),
            HourRow(1, UnitState.ForcedOutage,    plan: 1.0, spot: 500),
            HourRow(2, UnitState.MaintenanceOutage, plan: 1.0, spot: 500),
            HourRow(3, UnitState.MaintenanceOutage, plan: 1.0, spot: 500),
        };

        var events = DowntimeEventAggregator.Aggregate("drivdal", hours);

        events.Should().HaveCount(2);
        events[0].State.Should().Be(UnitState.ForcedOutage);
        events[0].Category.Should().Be(DowntimeEventCategory.TripFeil);
        events[1].State.Should().Be(UnitState.MaintenanceOutage);
        events[1].Category.Should().Be(DowntimeEventCategory.PlanlagtVedlikehold);
    }

    [Fact]
    public void Operlog_Overlap_Setter_HarOperlogMatch()
    {
        var hours = new[]
        {
            HourRow(0, UnitState.ForcedOutage, plan: 1.5, spot: 600),
            HourRow(1, UnitState.ForcedOutage, plan: 1.5, spot: 600),
        };
        var operlog = new[]
        {
            new ClassifiedEvent(
                Id: 1,
                OwnerOrgId: "org",
                PlantId: "drivdal",
                StartUtc: BaseUtc.AddMinutes(15),
                EndUtc: BaseUtc.AddMinutes(75),
                State: UnitState.ForcedOutage,
                CauseCode: "operlog:fault",
                Confidence: 0.95,
                SourcesJson: "[\"Operlog\"]",
                Rationale: "FEIL_AL"),
        };

        var events = DowntimeEventAggregator.Aggregate("drivdal", hours, operlog);

        events.Should().HaveCount(1);
        events[0].HarOperlogMatch.Should().BeTrue();
        events[0].CauseCode.Should().Be("operlog:fault");
    }

    [Fact]
    public void Plan_Null_Faller_Tilbake_Til_Spotbud_For_Tap()
    {
        var hours = new[]
        {
            HourRow(0, UnitState.ForcedOutage, plan: null, spot: 500, spotbud: 2.0),
        };

        var events = DowntimeEventAggregator.Aggregate("drivdal", hours);

        events[0].TapMwh.Should().Be(2.0);
        events[0].TapNok.Should().Be(2.0 * 500);
    }

    [Fact]
    public void MapCategory_Mapper_Forced_Outage_Til_TripFeil()
    {
        DowntimeEventAggregator.MapCategory(UnitState.ForcedOutage, "U1-UnplannedStop")
            .Should().Be(DowntimeEventCategory.TripFeil);
    }

    [Fact]
    public void MapCategory_Detekterer_Ekstern_Forstyrrelse_Fra_Cause_Code()
    {
        DowntimeEventAggregator.MapCategory(UnitState.ForcedOutage, "ext:gridfault")
            .Should().Be(DowntimeEventCategory.EksternForstyrrelse);
        DowntimeEventAggregator.MapCategory(UnitState.ForcedOutage, "nett-fall")
            .Should().Be(DowntimeEventCategory.EksternForstyrrelse);
    }

    [Fact]
    public void MapCategory_ReserveShutdown_Er_Markedstopp_Ikke_Nedetid()
    {
        DowntimeEventAggregator.MapCategory(UnitState.ReserveShutdown, "M1-NoCommitment")
            .Should().Be(DowntimeEventCategory.Markedstopp);
    }

    // --- helpers ---------------------------------------------------------------

    private static readonly DateTimeOffset BaseUtc =
        new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

    private static ClassifiedHourlyRow HourRow(
        int hourOffset,
        UnitState state,
        double? plan,
        double? spot,
        double? spotbud = null)
    {
        var time = BaseUtc.AddHours(hourOffset);
        var row = new SettlementHourlyRow
        {
            TimeUtc = time,
            TimeLocal = time,
            ProduksjonplanMwh = plan,
            SpotbudMwh = spotbud,
            SpotprisNokMwh = spot,
            MwhElhub = state == UnitState.InService ? 2.0 : 0,
        };
        return new ClassifiedHourlyRow
        {
            Row = row,
            State = state,
            CauseCode = state == UnitState.ForcedOutage ? "U1-UnplannedStop" : "0-Normal",
            Confidence = 0.9,
            Rationale = $"test {state}",
        };
    }
}
