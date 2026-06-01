using FluentAssertions;
using KraftverkUptime.Modules.Reporting.StartStopp;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.StartStopp;

/// <summary>
/// Spec NESTE-CHAT-START-STOPP-KPI.md (2026-05-22): ren-funksjons-tester
/// for syklus-tellingen. Dekker grunnscenarier (gjentatte starter, ren
/// drift uten avbrudd, og hvilemodus-utslipp som ikke skal telles).
/// </summary>
public class StartStoppCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private static IEnumerable<HourlyProduction> MakeSequence(params double[] mwh) =>
        mwh.Select((v, i) => new HourlyProduction(T0.AddHours(i), v));

    [Fact]
    public void CountStarts_FiveStartsAndStops_Returns5()
    {
        // Sekvensen: 0,1,1,0,1,1,0,1,1,0,1,0,1,0
        // Transisjoner 0→1 ved: i=1, i=4, i=7, i=10, i=12 = 5 starter.
        var hours = MakeSequence(0, 1, 1, 0, 1, 1, 0, 1, 1, 0, 1, 0, 1, 0);

        StartStoppCalculator.CountStarts(hours, installedMw: 5).Should().Be(5);
    }

    [Fact]
    public void CountStarts_RunningOnly_Returns1()
    {
        // Anlegget starter i første time over terskel og er aldri under
        // siden — én syklus.
        var hours = MakeSequence(1, 1, 1, 1, 1);

        StartStoppCalculator.CountStarts(hours, installedMw: 5).Should().Be(1);
    }

    [Fact]
    public void CountStarts_BelowTolerance_Ignored()
    {
        // 5 MW × 0,5 % = 0,025 MWh terskel.
        // 0,01 MWh er UNDER terskel → ikke drift → ignoreres.
        // Eneste reelle oppstart ved i=4 (0→1).
        var hours = MakeSequence(0, 0.01, 0.01, 0, 1, 1);

        StartStoppCalculator.CountStarts(hours, installedMw: 5).Should().Be(1);
    }

    [Fact]
    public void CountStarts_EmptySequence_Returns0()
    {
        StartStoppCalculator.CountStarts(Array.Empty<HourlyProduction>(), installedMw: 5)
            .Should().Be(0);
    }

    [Fact]
    public void CountStarts_NullMwhTreatedAsZero()
    {
        // null = ukjent → behandles som 0 (ikke drift). Sekvens:
        // null,1,null,1 → starter ved i=1 og i=3 = 2 syklus.
        var hours = new[]
        {
            new HourlyProduction(T0.AddHours(0), null),
            new HourlyProduction(T0.AddHours(1), 1.0),
            new HourlyProduction(T0.AddHours(2), null),
            new HourlyProduction(T0.AddHours(3), 1.0),
        };

        StartStoppCalculator.CountStarts(hours, installedMw: 5).Should().Be(2);
    }

    [Fact]
    public void CountStarts_ZeroOrNegativeInstalledMw_Returns0()
    {
        // Hvis installert effekt ikke er satt (0), kan vi ikke beregne
        // terskel meningsfylt — vi returnerer 0 istedenfor å feile.
        var hours = MakeSequence(0, 1, 1, 0, 1);

        StartStoppCalculator.CountStarts(hours, installedMw: 0).Should().Be(0);
        StartStoppCalculator.CountStarts(hours, installedMw: -1).Should().Be(0);
    }

    [Fact]
    public void CountStarts_UnsortedInput_StillReturnsCorrect()
    {
        // Kalkulatoren skal sortere kronologisk uavhengig av input-rekkefølgen.
        var hours = new[]
        {
            new HourlyProduction(T0.AddHours(3), 1.0),
            new HourlyProduction(T0.AddHours(1), 1.0),
            new HourlyProduction(T0.AddHours(0), 0.0),
            new HourlyProduction(T0.AddHours(2), 0.0),
        };

        // Sortert: 0, 1, 1, 0, 1, 1 → start ved i=1 og i=3 = 2 syklus.
        // Vent — sortert er [0:0, 1:1, 2:0, 3:1] → 0→1 ved i=1 og 0→1 ved i=3.
        StartStoppCalculator.CountStarts(hours, installedMw: 5).Should().Be(2);
    }
}
