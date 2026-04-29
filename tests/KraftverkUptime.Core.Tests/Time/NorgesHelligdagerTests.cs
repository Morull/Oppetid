using FluentAssertions;
using KraftverkUptime.Core.Time;
using Xunit;

namespace KraftverkUptime.Core.Tests.Time;

public class NorgesHelligdagerTests
{
    [Theory]
    [InlineData(2025, 4, 20)]   // Påskedag 2025
    [InlineData(2026, 4, 5)]    // Påskedag 2026
    [InlineData(2027, 3, 28)]   // Påskedag 2027
    [InlineData(2028, 4, 16)]   // Påskedag 2028
    public void Paskedag_Beregnes_Korrekt(int year, int month, int day)
    {
        NorgesHelligdager.Paskedag(year)
            .Should().Be(new DateOnly(year, month, day));
    }

    [Fact]
    public void Faste_Helligdager_2026_Er_Inkludert()
    {
        var dager = NorgesHelligdager.ForYear(2026);

        dager.Should().Contain(new DateOnly(2026, 1, 1));   // Nyttårsdag
        dager.Should().Contain(new DateOnly(2026, 5, 1));   // Off. høytidsdag
        dager.Should().Contain(new DateOnly(2026, 5, 17));  // Grunnlovsdag
        dager.Should().Contain(new DateOnly(2026, 12, 25)); // 1. juledag
        dager.Should().Contain(new DateOnly(2026, 12, 26)); // 2. juledag
    }

    [Fact]
    public void Bevegelige_Helligdager_2026_Beregnes_Korrekt()
    {
        // Påskedag 2026 = 5. april
        var dager = NorgesHelligdager.ForYear(2026);

        dager.Should().Contain(new DateOnly(2026, 4, 2));   // Skjærtorsdag
        dager.Should().Contain(new DateOnly(2026, 4, 3));   // Langfredag
        dager.Should().Contain(new DateOnly(2026, 4, 5));   // 1. påskedag
        dager.Should().Contain(new DateOnly(2026, 4, 6));   // 2. påskedag
        dager.Should().Contain(new DateOnly(2026, 5, 14));  // Kr.himmelfartsdag (påske + 39)
        dager.Should().Contain(new DateOnly(2026, 5, 24));  // 1. pinsedag (påske + 49)
        dager.Should().Contain(new DateOnly(2026, 5, 25));  // 2. pinsedag (påske + 50)
    }

    [Fact]
    public void ErHelligdag_Returnerer_True_For_Roede_Dager()
    {
        NorgesHelligdager.ErHelligdag(new DateOnly(2026, 5, 17)).Should().BeTrue();
        NorgesHelligdager.ErHelligdag(new DateOnly(2026, 4, 5)).Should().BeTrue(); // Påskedag
    }

    [Fact]
    public void ErHelligdag_Returnerer_False_For_Vanlige_Dager()
    {
        NorgesHelligdager.ErHelligdag(new DateOnly(2026, 2, 4)).Should().BeFalse(); // Onsdag
        NorgesHelligdager.ErHelligdag(new DateOnly(2026, 6, 1)).Should().BeFalse(); // Vanlig mandag
    }

    [Fact]
    public void ErHelligdag_Returnerer_False_For_Vanlige_Sondager()
    {
        // En vanlig søndag er IKKE helligdag, men er søndag.
        var sondag = new DateOnly(2026, 2, 8);
        sondag.DayOfWeek.Should().Be(DayOfWeek.Sunday);
        NorgesHelligdager.ErHelligdag(sondag).Should().BeFalse();
        NorgesHelligdager.ErHelligdagEllerSondag(sondag).Should().BeTrue();
    }

    [Fact]
    public void Cache_Returnerer_Samme_Instans_For_Samme_Aar()
    {
        var a = NorgesHelligdager.ForYear(2026);
        var b = NorgesHelligdager.ForYear(2026);
        a.Should().BeSameAs(b);
    }
}
