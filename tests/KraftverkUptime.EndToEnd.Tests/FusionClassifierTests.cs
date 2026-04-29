using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Classification;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Settlement.Dtos;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Tester for FusionClassifier (Steg 5): konfliktløsning mellom settlement,
/// SCADA og operlog.
/// </summary>
public class FusionClassifierTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

    private static ClassifiedHourlyRow Settlement(int hour, UnitState state,
        string cause = "settlement", double confidence = 0.85) =>
        new()
        {
            Row = new SettlementHourlyRow
            {
                TimeUtc = T0.AddHours(hour),
                TimeLocal = T0.AddHours(hour),
            },
            State = state,
            CauseCode = cause,
            Confidence = confidence,
            Rationale = "settlement-rationale",
        };

    private static Dictionary<DateTimeOffset, ScadaFusionInput> Scada(
        params (int Hour, UnitState State, string Cause)[] entries)
    {
        var dict = new Dictionary<DateTimeOffset, ScadaFusionInput>();
        foreach (var (hour, state, cause) in entries)
        {
            dict[T0.AddHours(hour)] = new ScadaFusionInput(
                State: state, Confidence: 0.9, CauseCode: cause,
                Rationale: $"scada-rationale-{hour}");
        }
        return dict;
    }

    private static ClassifiedEvent Operlog(int hour, int minute, string cause) =>
        new(Id: 0, OwnerOrgId: "dev-org", PlantId: "test",
            StartUtc: T0.AddHours(hour).AddMinutes(minute), EndUtc: null,
            State: UnitState.ForcedOutage, CauseCode: cause, Confidence: 0.9,
            SourcesJson: "[\"operlog\"]", Rationale: null);

    [Fact]
    public void Fuse_UtenScadaOgOperlog_ReturnererSettlementUendret()
    {
        var input = new[] { Settlement(0, UnitState.InService) };
        var result = FusionClassifier.Fuse(input);

        result.Should().HaveCount(1);
        result[0].State.Should().Be(UnitState.InService);
        result[0].Confidence.Should().Be(0.85);
        result[0].Rationale.Should().Be("settlement-rationale");
    }

    [Fact]
    public void Fuse_SettlementOgScadaEnige_BehoderState_OgBoosterConfidence()
    {
        var input = new[] { Settlement(0, UnitState.InService, confidence: 0.85) };
        var scada = Scada((0, UnitState.InService, "scada:in_service"));

        var result = FusionClassifier.Fuse(input, scada);

        result[0].State.Should().Be(UnitState.InService);
        result[0].Confidence.Should().BeApproximately(0.95, 1e-9); // 0.9 + 0.05
        result[0].Rationale.Should().Contain("scada-bekreftet");
    }

    [Fact]
    public void Fuse_SettlementOgScadaUenige_ScadaVinner()
    {
        // Settlement ser InService (Elhub > 0), SCADA ser ResourceUnavailable
        // (magasin på LRV). SCADA er nærmere virkeligheten — den skal vinne.
        var input = new[] { Settlement(0, UnitState.InService) };
        var scada = Scada((0, UnitState.ResourceUnavailable, "scada:resource_unavailable"));

        var result = FusionClassifier.Fuse(input, scada);

        result[0].State.Should().Be(UnitState.ResourceUnavailable);
        result[0].Confidence.Should().Be(0.9); // SCADA's confidence
        result[0].Rationale.Should().Contain("settlement=InService");
        result[0].Rationale.Should().Contain("scada=ResourceUnavailable");
    }

    [Fact]
    public void Fuse_OperlogEventBerikerCauseCode()
    {
        var input = new[] { Settlement(0, UnitState.ForcedOutage, cause: "U1-UnplannedStop") };
        var operlog = new[] { Operlog(0, 23, "operlog:fault") };

        var result = FusionClassifier.Fuse(input, scadaByHour: null, operlog: operlog);

        result[0].State.Should().Be(UnitState.ForcedOutage); // ikke endret
        result[0].CauseCode.Should().Be("operlog:operlog:fault"); // operlog-tag prefiks
        result[0].Rationale.Should().Contain("operlog:");
    }

    [Fact]
    public void Fuse_OperlogIkkeIDenneTimen_RoererIkkeRow()
    {
        var input = new[]
        {
            Settlement(0, UnitState.ForcedOutage),
            Settlement(1, UnitState.InService),
        };
        // Operlog-event på time 0 — skal ikke berike time 1
        var operlog = new[] { Operlog(0, 30, "operlog:fault") };

        var result = FusionClassifier.Fuse(input, scadaByHour: null, operlog: operlog);

        result[0].CauseCode.Should().StartWith("operlog:");
        result[1].CauseCode.Should().Be("settlement"); // uendret
    }

    [Fact]
    public void Fuse_AlleTreKilder_KombinererKorrekt()
    {
        // Settlement: ReserveShutdown (Elhub=0, Spotbud=0)
        // SCADA: ResourceUnavailable (magasin tom)
        // Operlog: STOPPER_AL kl. 12:15
        // Forventet: state=ResourceUnavailable (SCADA vinner over settlement),
        // cause beriket av operlog, rationale viser hele konflikten.
        var input = new[] { Settlement(0, UnitState.ReserveShutdown) };
        var scada = Scada((0, UnitState.ResourceUnavailable, "scada:resource_unavailable"));
        var operlog = new[] { Operlog(0, 15, "operlog:stopper") };

        var result = FusionClassifier.Fuse(input, scada, operlog);

        result[0].State.Should().Be(UnitState.ResourceUnavailable);
        result[0].CauseCode.Should().Be("operlog:operlog:stopper");
        result[0].Rationale.Should().Contain("settlement=ReserveShutdown");
        result[0].Rationale.Should().Contain("scada=ResourceUnavailable");
        result[0].Rationale.Should().Contain("operlog:");
    }

    [Fact]
    public void Fuse_TomtInput_GirTomResultat()
    {
        var result = FusionClassifier.Fuse(Array.Empty<ClassifiedHourlyRow>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void Fuse_BevarerRowDataIKonflikt()
    {
        // Også når SCADA overstyrer state, skal vi beholde settlement.Row
        // (f.eks. MwhElhub) for økonomisk videre-prosessering.
        var settlementRow = Settlement(0, UnitState.InService) with
        {
            Row = new SettlementHourlyRow
            {
                TimeUtc = T0,
                TimeLocal = T0,
                MwhElhub = 1.5,
                SpotbudMwh = 1.5,
                SpotomsetningNok = 1500,
            },
        };
        var input = new[] { settlementRow };
        var scada = Scada((0, UnitState.ForcedOutage, "scada:committed_no_delivery"));

        var result = FusionClassifier.Fuse(input, scada);

        result[0].State.Should().Be(UnitState.ForcedOutage);
        result[0].Row.MwhElhub.Should().Be(1.5);
        result[0].Row.SpotomsetningNok.Should().Be(1500);
    }
}
