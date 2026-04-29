using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Scada.Classification;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Scada;

/// <summary>
/// Verifiserer hver av de 9 UnitState-tilstandene som <see cref="ScadaClassifier"/>
/// kan produsere, samt prioritert rekkefølge mellom reglene.
/// </summary>
public class ScadaClassifierTests
{
    private static readonly DateTimeOffset T0 = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

    private static ScadaClassifierOptions Options(double capacityKw = 2200) =>
        new() { InstalledCapacityKw = capacityKw };

    private static ClassifiedEvent MakeEvent(DateTimeOffset start, UnitState state, string causeCode) =>
        new(Id: 0, OwnerOrgId: "dev-org", PlantId: "test",
            StartUtc: start, EndUtc: null,
            State: state, CauseCode: causeCode, Confidence: 0.9,
            SourcesJson: "[\"operlog\"]", Rationale: null);

    private static ScadaClassifiedHour Single(ScadaHourlyInput input,
        IReadOnlyList<ClassifiedEvent>? operlog = null,
        ScadaClassifierOptions? options = null)
    {
        return ScadaClassifier.Classify(new[] { input }, options ?? Options(), operlog)[0];
    }

    [Fact]
    public void CommunicationAlarm_ProduserrInformationUnavailable()
    {
        var r = Single(new ScadaHourlyInput
        {
            TimeUtc = T0,
            CommunicationAlarm = true,
            PowerKw = 0,
        });

        r.State.Should().Be(UnitState.InformationUnavailable);
        r.CauseCode.Should().Be("scada:com_alarm");
    }

    [Fact]
    public void Spinning_OgFullProduksjon_GirInService()
    {
        var r = Single(new ScadaHourlyInput
        {
            TimeUtc = T0,
            RpmAvg = 750,
            PowerKw = 2100, // 95.5% av 2200
        });

        r.State.Should().Be(UnitState.InService);
        r.CauseCode.Should().Be("scada:in_service");
    }

    [Fact]
    public void Spinning_OgRedusertProduksjon_GirForcedDerating()
    {
        var r = Single(new ScadaHourlyInput
        {
            TimeUtc = T0,
            RpmAvg = 750,
            PowerKw = 1500, // 68% av 2200 — i partial-derating-sjiktet
        });

        r.State.Should().Be(UnitState.ForcedDerating);
        r.CauseCode.Should().Be("scada:partial_derating");
    }

    [Fact]
    public void Spinning_OgSterkDerating_GirForcedDeratingMedHoyConfidence()
    {
        var r = Single(new ScadaHourlyInput
        {
            TimeUtc = T0,
            RpmAvg = 750,
            PowerKw = 800, // 36% av 2200
        });

        r.State.Should().Be(UnitState.ForcedDerating);
        r.CauseCode.Should().Be("scada:strong_derating");
        r.Confidence.Should().BeGreaterThan(0.80);
    }

    [Fact]
    public void Stillstand_OgMagasinPaaLRV_GirResourceUnavailable()
    {
        var r = Single(new ScadaHourlyInput
        {
            TimeUtc = T0,
            RpmAvg = 0,
            PowerKw = 0,
            UpstreamLevelMoh = 100.02,
            LowestRegulatedLevelMoh = 100.0,
        });

        r.State.Should().Be(UnitState.ResourceUnavailable);
        r.CauseCode.Should().Be("scada:resource_unavailable");
    }

    [Fact]
    public void Stillstand_MedOperlogFault_GirForcedOutage()
    {
        var fault = MakeEvent(T0.AddMinutes(15), UnitState.ForcedOutage, "operlog:fault");

        var r = Single(
            new ScadaHourlyInput { TimeUtc = T0, RpmAvg = 0, PowerKw = 0 },
            operlog: new[] { fault });

        r.State.Should().Be(UnitState.ForcedOutage);
        r.CauseCode.Should().Be("operlog:fault");
    }

    [Fact]
    public void Stillstand_MedOperlogPlanlagt_GirPlannedOutage()
    {
        var planned = MakeEvent(T0.AddMinutes(5), UnitState.PlannedOutage, "operlog:planned_revision");

        var r = Single(
            new ScadaHourlyInput { TimeUtc = T0, RpmAvg = 0, PowerKw = 0 },
            operlog: new[] { planned });

        r.State.Should().Be(UnitState.PlannedOutage);
        r.CauseCode.Should().Be("operlog:planned");
    }

    [Fact]
    public void Stillstand_MedOperlogManualStop_GirMaintenanceOutage()
    {
        var manual = MakeEvent(T0.AddMinutes(30), UnitState.MaintenanceOutage, "operlog:stop");

        var r = Single(
            new ScadaHourlyInput { TimeUtc = T0, RpmAvg = 0, PowerKw = 0 },
            operlog: new[] { manual });

        r.State.Should().Be(UnitState.MaintenanceOutage);
        r.CauseCode.Should().Be("operlog:manual_stop");
    }

    [Fact]
    public void Stillstand_MedSpotbud_GirForcedOutage()
    {
        var r = Single(new ScadaHourlyInput
        {
            TimeUtc = T0,
            RpmAvg = 0,
            PowerKw = 0,
            SpotbudMwh = 1.5,
        });

        r.State.Should().Be(UnitState.ForcedOutage);
        r.CauseCode.Should().Be("scada:committed_no_delivery");
    }

    [Fact]
    public void Stillstand_UtenSpotbud_OgMagasinOk_GirReserveShutdown()
    {
        var r = Single(new ScadaHourlyInput
        {
            TimeUtc = T0,
            RpmAvg = 0,
            PowerKw = 0,
            SpotbudMwh = 0,
            UpstreamLevelMoh = 102.0,
            LowestRegulatedLevelMoh = 100.0,
        });

        r.State.Should().Be(UnitState.ReserveShutdown);
        r.CauseCode.Should().Be("scada:reserve_shutdown");
    }

    [Fact]
    public void CommunicationAlarm_HarPrioritetOverAlt()
    {
        // Selv med Spotbud + LRV-grense skal com_alarm vinne
        var r = Single(new ScadaHourlyInput
        {
            TimeUtc = T0,
            CommunicationAlarm = true,
            RpmAvg = 0,
            PowerKw = 0,
            SpotbudMwh = 1.5,
            UpstreamLevelMoh = 100.01,
            LowestRegulatedLevelMoh = 100.0,
        });

        r.State.Should().Be(UnitState.InformationUnavailable);
    }

    [Fact]
    public void IngenKapasitet_FaltTilbakeTilInServiceVedKjorende()
    {
        // Hvis InstalledCapacityKw=0 (manglende oppsett) kan ikke derating
        // utledes. Faller tilbake til InService som "minst dårlig" default.
        var r = Single(
            new ScadaHourlyInput { TimeUtc = T0, RpmAvg = 750, PowerKw = 500 },
            options: new ScadaClassifierOptions { InstalledCapacityKw = 0 });

        r.State.Should().Be(UnitState.InService);
        r.Confidence.Should().BeApproximately(0.85, 0.001);
    }

    [Fact]
    public void Klassifiserer_FlereTimerIRekkefoelge()
    {
        var inputs = new[]
        {
            new ScadaHourlyInput { TimeUtc = T0, RpmAvg = 750, PowerKw = 2100 },
            new ScadaHourlyInput { TimeUtc = T0.AddHours(1), RpmAvg = 0, PowerKw = 0, SpotbudMwh = 1 },
            new ScadaHourlyInput { TimeUtc = T0.AddHours(2), CommunicationAlarm = true, PowerKw = 0 },
        };

        var result = ScadaClassifier.Classify(inputs, Options());
        result.Should().HaveCount(3);
        result[0].State.Should().Be(UnitState.InService);
        result[1].State.Should().Be(UnitState.ForcedOutage);
        result[2].State.Should().Be(UnitState.InformationUnavailable);
    }
}
