using FluentAssertions;
using KraftverkUptime.Modules.Reporting.Economy;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Economy;

/// <summary>
/// Spec NESTE-CHAT-OKONOMI-FANE-PDF.md (2026-05-22, del "Forrige periode"):
/// dekker alle fem PeriodKind-verdier samt edge-cases (måneds-overgang,
/// skuddår, kort-måned-overgang). Ren funksjon — ingen mocking, alt
/// kjører deterministisk.
/// </summary>
public class PreviousPeriodCalculatorTests
{
    private static DateTimeOffset Utc(int year, int month, int day) =>
        new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------------
    // Måned: April 2026 → Mars 2026
    // ------------------------------------------------------------------------

    [Fact]
    public void Month_April_ReturnsMarch()
    {
        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(
            from: Utc(2026, 4, 1),
            to: Utc(2026, 5, 1),
            kind: PeriodKind.Month);

        prevFrom.Should().Be(Utc(2026, 3, 1));
        prevTo.Should().Be(Utc(2026, 4, 1));
    }

    [Fact]
    public void Month_January_WrapsToPreviousDecember()
    {
        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(
            from: Utc(2026, 1, 1),
            to: Utc(2026, 2, 1),
            kind: PeriodKind.Month);

        prevFrom.Should().Be(Utc(2025, 12, 1));
        prevTo.Should().Be(Utc(2026, 1, 1));
    }

    [Fact]
    public void Month_March_PreviousFebruary_HandlerKortMaaned()
    {
        // Mars (31 dager) → Februar (28/29 dager). AddMonths kappes naturlig
        // til siste gyldige dato — vi mister ikke en dag.
        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(
            from: Utc(2026, 3, 1),
            to: Utc(2026, 4, 1),
            kind: PeriodKind.Month);

        prevFrom.Should().Be(Utc(2026, 2, 1));
        prevTo.Should().Be(Utc(2026, 3, 1));
    }

    // ------------------------------------------------------------------------
    // Kvartal: Q2 2026 → Q1 2026
    // ------------------------------------------------------------------------

    [Fact]
    public void Quarter_Q2_ReturnsQ1()
    {
        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(
            from: Utc(2026, 4, 1),
            to: Utc(2026, 7, 1),
            kind: PeriodKind.Quarter);

        prevFrom.Should().Be(Utc(2026, 1, 1));
        prevTo.Should().Be(Utc(2026, 4, 1));
    }

    [Fact]
    public void Quarter_Q1_WrapsToPreviousQ4()
    {
        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(
            from: Utc(2026, 1, 1),
            to: Utc(2026, 4, 1),
            kind: PeriodKind.Quarter);

        prevFrom.Should().Be(Utc(2025, 10, 1));
        prevTo.Should().Be(Utc(2026, 1, 1));
    }

    // ------------------------------------------------------------------------
    // År: 2026 → 2025
    // ------------------------------------------------------------------------

    [Fact]
    public void Year_2026_Returns2025()
    {
        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(
            from: Utc(2026, 1, 1),
            to: Utc(2027, 1, 1),
            kind: PeriodKind.Year);

        prevFrom.Should().Be(Utc(2025, 1, 1));
        prevTo.Should().Be(Utc(2026, 1, 1));
    }

    // ------------------------------------------------------------------------
    // Hittil i år: 1.1–22.5.2026 → 1.1–22.5.2025
    // ------------------------------------------------------------------------

    [Fact]
    public void YearToDate_SammeDatoSpennIFjor()
    {
        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(
            from: Utc(2026, 1, 1),
            to: Utc(2026, 5, 22),
            kind: PeriodKind.YearToDate);

        prevFrom.Should().Be(Utc(2025, 1, 1));
        prevTo.Should().Be(Utc(2025, 5, 22));
    }

    [Fact]
    public void YearToDate_SkuddårTilIkkeSkuddår_TaperLeapDay()
    {
        // 1.1-29.2.2024 (skuddår). I 2023 finnes ikke 29. feb — AddYears(-1)
        // kapper til 28. feb. Tester at vi får DET som forventet utfall
        // (ikke en exception eller annen overraskelse).
        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(
            from: Utc(2024, 1, 1),
            to: Utc(2024, 2, 29),
            kind: PeriodKind.YearToDate);

        prevFrom.Should().Be(Utc(2023, 1, 1));
        prevTo.Should().Be(Utc(2023, 2, 28));
    }

    // ------------------------------------------------------------------------
    // Egendefinert: samme lengde umiddelbart før valgt periode
    // ------------------------------------------------------------------------

    [Fact]
    public void Custom_SammeLengdeUmiddelbartFoer()
    {
        // 1.5-15.5.2026 = 14 dager. Forrige = 17.4-1.5.2026 (også 14 dager,
        // slutter presis der nåværende begynner).
        var from = Utc(2026, 5, 1);
        var to = Utc(2026, 5, 15);
        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(
            from, to, PeriodKind.Custom);

        prevFrom.Should().Be(Utc(2026, 4, 17));
        prevTo.Should().Be(from);
        (prevTo - prevFrom).Should().Be(to - from,
            "forrige periode skal ha eksakt samme lengde som nåværende.");
    }

    [Fact]
    public void Custom_AvKortVarighet_BeholderTimerOgMinutter()
    {
        // Custom-perioder kan være under en dag. Verifiserer at vi ikke
        // mister sub-day-presisjon.
        var from = new DateTimeOffset(2026, 5, 22, 8, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 5, 22, 18, 30, 0, TimeSpan.Zero);
        var (prevFrom, prevTo) = PreviousPeriodCalculator.Calculate(
            from, to, PeriodKind.Custom);

        prevTo.Should().Be(from);
        prevFrom.Should().Be(new DateTimeOffset(2026, 5, 21, 21, 30, 0, TimeSpan.Zero),
            "forrige periode = 10 t 30 min umiddelbart før kl. 08:00 = 21:30 dagen før.");
    }

    // ------------------------------------------------------------------------
    // Argument-validering
    // ------------------------------------------------------------------------

    [Fact]
    public void Throws_NaarToErLikFrom()
    {
        var t = Utc(2026, 5, 1);
        Action call = () => PreviousPeriodCalculator.Calculate(t, t, PeriodKind.Month);
        call.Should().Throw<ArgumentException>().WithParameterName("to");
    }

    [Fact]
    public void Throws_NaarToErFoerFrom()
    {
        Action call = () => PreviousPeriodCalculator.Calculate(
            Utc(2026, 5, 1), Utc(2026, 4, 1), PeriodKind.Month);
        call.Should().Throw<ArgumentException>().WithParameterName("to");
    }

    [Fact]
    public void Throws_NaarKindErUkjent()
    {
        Action call = () => PreviousPeriodCalculator.Calculate(
            Utc(2026, 4, 1), Utc(2026, 5, 1), (PeriodKind)999);
        call.Should().Throw<ArgumentOutOfRangeException>();
    }
}
