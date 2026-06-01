using FluentAssertions;
using KraftverkUptime.Infrastructure.Reporting;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// Tester at overlappende importer kollapses til én per logiske periode, slik
/// at aggregerte KPI-er (spotomsetning, oppgjør, KAIA) ikke dobbelttelles.
/// Regresjon for live-funn 2026-05-30: Vikeså «mai 1–17» + «mai 1–20».
/// </summary>
public class OverlappingImportResolverTests
{
    private sealed record Imp(DateTimeOffset Start, DateTimeOffset End, DateTimeOffset Imported);

    private static DateTimeOffset D(int y, int m, int d) => new(new DateTime(y, m, d), TimeSpan.Zero);

    private static IReadOnlyList<Imp> Resolve(IEnumerable<Imp> imports) =>
        OverlappingImportResolver.ResolveNonOverlapping(
            imports, i => i.Start, i => i.End, i => i.Imported);

    [Fact]
    public void Contained_Reimport_Is_Collapsed_To_Widest()
    {
        // «mai 1–17» er fullstendig inneholdt i «mai 1–20» → kun den brede beholdes.
        var kort = new Imp(D(2026, 5, 1), D(2026, 5, 17), D(2026, 5, 29));
        var bred = new Imp(D(2026, 5, 1), D(2026, 5, 20), D(2026, 5, 21));

        var result = Resolve(new[] { kort, bred });

        result.Should().ContainSingle().Which.Should().Be(bred);
    }

    [Fact]
    public void Distinct_Months_Are_All_Kept()
    {
        var jan = new Imp(D(2026, 1, 1), D(2026, 1, 31), D(2026, 2, 1));
        var feb = new Imp(D(2026, 2, 1), D(2026, 2, 28), D(2026, 3, 1));
        var mar = new Imp(D(2026, 3, 1), D(2026, 3, 31), D(2026, 4, 1));

        var result = Resolve(new[] { mar, jan, feb });

        result.Should().HaveCount(3);
        result.Should().BeEquivalentTo(new[] { jan, feb, mar });
    }

    [Fact]
    public void Exact_Duplicate_Keeps_Newest_Import()
    {
        var gammel = new Imp(D(2026, 1, 1), D(2026, 1, 31), D(2026, 1, 5));
        var ny = new Imp(D(2026, 1, 1), D(2026, 1, 31), D(2026, 1, 9));

        var result = Resolve(new[] { gammel, ny });

        result.Should().ContainSingle().Which.Imported.Should().Be(ny.Imported);
    }

    [Fact]
    public void Empty_Input_Returns_Empty()
    {
        Resolve(Array.Empty<Imp>()).Should().BeEmpty();
    }
}
