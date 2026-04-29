using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.Nedetid;
using KraftverkUptime.Modules.Settlement.Dtos;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Nedetid;

/// <summary>
/// Verifiserer at <see cref="RistAlarmDetector"/> finner rist-relaterte
/// trip-events innen ±60 min, og at <see cref="DowntimeEventAggregator"/>
/// re-kategoriserer disse til <c>TettInntaksrist</c>.
/// </summary>
public class RistDetectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

    private static ClassifiedEvent OperlogEvent(int hourOffset, int minuteOffset, string causeCode) =>
        new(Id: 0, OwnerOrgId: "dev-org", PlantId: "test",
            StartUtc: T0.AddHours(hourOffset).AddMinutes(minuteOffset),
            EndUtc: null, State: UnitState.ForcedOutage,
            CauseCode: causeCode, Confidence: 0.9,
            SourcesJson: "[\"operlog\"]", Rationale: null);

    private static ClassifiedHourlyRow Hour(int hourOffset, UnitState state) =>
        new()
        {
            Row = new SettlementHourlyRow
            {
                TimeUtc = T0.AddHours(hourOffset),
                TimeLocal = T0.AddHours(hourOffset),
                MwhElhub = state == UnitState.InService ? 1.0 : 0.0,
                SpotbudMwh = 1.0,
            },
            State = state, CauseCode = "test", Confidence = 1.0, Rationale = "test",
        };

    [Fact]
    public void ExtractRistAlarmTimes_FinnerKunRistFalltap_OperlogEvents()
    {
        var detector = new RistAlarmDetector();
        var events = new[]
        {
            OperlogEvent(0, 0, "operlog:start"),
            OperlogEvent(1, 0, "operlog:rist-falltap"),
            OperlogEvent(2, 0, "operlog:fault"),
            OperlogEvent(3, 0, "operlog:rist-falltap"),
        };

        var times = detector.ExtractRistAlarmTimes(events);

        times.Should().HaveCount(2);
        times[0].Should().Be(T0.AddHours(1));
        times[1].Should().Be(T0.AddHours(3));
    }

    [Fact]
    public void IsRistRelated_TripInnenfor60Min_FraRistAlarm_GirTrue()
    {
        var detector = new RistAlarmDetector();
        var alarm = T0.AddMinutes(0);
        var trip = T0.AddMinutes(45);

        detector.IsRistRelated(trip, new[] { alarm }).Should().BeTrue();
    }

    [Fact]
    public void IsRistRelated_TripUtenfor60Min_GirFalse()
    {
        var detector = new RistAlarmDetector();
        var alarm = T0;
        var trip = T0.AddMinutes(75);

        detector.IsRistRelated(trip, new[] { alarm }).Should().BeFalse();
    }

    [Fact]
    public void IsRistRelated_FlereAlarmer_FinnerHvilkenSomHelstInnenforVindu()
    {
        var detector = new RistAlarmDetector();
        var alarms = new[] { T0, T0.AddHours(5), T0.AddHours(10) };
        var trip = T0.AddHours(10).AddMinutes(-15);

        detector.IsRistRelated(trip, alarms).Should().BeTrue();
    }

    [Fact]
    public void IsRistRelated_TomtAlarm_OgRist_GirFalse()
    {
        var detector = new RistAlarmDetector();
        detector.IsRistRelated(T0, Array.Empty<DateTimeOffset>()).Should().BeFalse();
    }

    [Fact]
    public void Aggregate_TripMedRistAlarmInnen60Min_KategoriseresSomTettInntaksrist()
    {
        // En FO-time + rist-alarm 30 min senere → TettInntaksrist
        var hours = new[] { Hour(0, UnitState.ForcedOutage) };
        var operlog = new[] { OperlogEvent(0, 30, "operlog:rist-falltap") };

        var events = DowntimeEventAggregator.Aggregate("test", hours, operlog);

        events.Should().HaveCount(1);
        events[0].Category.Should().Be(DowntimeEventCategory.TettInntaksrist);
        events[0].CauseCode.Should().Be("operlog:rist-falltap");
    }

    [Fact]
    public void Aggregate_TripUtenRistAlarm_BehoderTripFeil()
    {
        var hours = new[] { Hour(0, UnitState.ForcedOutage) };
        var operlog = new[] { OperlogEvent(0, 30, "operlog:fault") };

        var events = DowntimeEventAggregator.Aggregate("test", hours, operlog);

        events[0].Category.Should().Be(DowntimeEventCategory.TripFeil);
    }

    [Fact]
    public void Aggregate_RistAlarmLangtFra_TripBehoderTripFeil()
    {
        // Rist-alarm 90 min etter trip → utenfor 60-min-vinduet, ikke rist
        var hours = new[] { Hour(0, UnitState.ForcedOutage) };
        var operlog = new[] { OperlogEvent(1, 30, "operlog:rist-falltap") };

        var events = DowntimeEventAggregator.Aggregate("test", hours, operlog);

        events[0].Category.Should().Be(DowntimeEventCategory.TripFeil);
    }

    [Fact]
    public void Aggregate_PlannedOutageMedRistAlarm_PaavirkesIkke()
    {
        // Rist-deteksjon endrer kun trip-relaterte states (FO/FD).
        // PlannedOutage forblir PlanlagtVedlikehold uavhengig av rist-alarm.
        var hours = new[] { Hour(0, UnitState.PlannedOutage) };
        var operlog = new[] { OperlogEvent(0, 30, "operlog:rist-falltap") };

        var events = DowntimeEventAggregator.Aggregate("test", hours, operlog);

        events[0].Category.Should().Be(DowntimeEventCategory.PlanlagtVedlikehold);
    }

    [Fact]
    public void Aggregate_FlereTrips_KunRistRelaterte_OmKategoriseres()
    {
        var hours = new[]
        {
            Hour(0, UnitState.ForcedOutage),       // har rist-alarm i nærheten
            Hour(1, UnitState.InService),          // ikke nedetid
            Hour(2, UnitState.ForcedOutage),       // ingen rist-alarm i nærheten
        };
        var operlog = new[]
        {
            OperlogEvent(0, 15, "operlog:rist-falltap"),
            // ingen rist-alarm rundt time 2
        };

        var events = DowntimeEventAggregator.Aggregate("test", hours, operlog);

        events.Should().HaveCount(2);
        events[0].Category.Should().Be(DowntimeEventCategory.TettInntaksrist);
        events[1].Category.Should().Be(DowntimeEventCategory.TripFeil);
    }

    [Fact]
    public void Aggregate_KonfigurerbartVindu_30Min_StrengereDeteksjon()
    {
        var detector = new RistAlarmDetector(new RistDetectionOptions { WindowMinutes = 30 });

        // Trip 45 min etter rist-alarm → utenfor 30-min-vindu, ikke rist
        var hours = new[] { Hour(0, UnitState.ForcedOutage) };
        var operlog = new[] { OperlogEvent(-1, 15, "operlog:rist-falltap") }; // 45 min før T0

        var events = DowntimeEventAggregator.Aggregate("test", hours, operlog, detector);

        events[0].Category.Should().Be(DowntimeEventCategory.TripFeil);
    }
}
