using FluentAssertions;
using KraftverkUptime.Modules.Reporting.KaiaCost;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.KaiaCost;

/// <summary>
/// Unit-tester for <see cref="KaiaFeeProration.Compute"/> — den rene
/// pro-rata-funksjonen som er kjernen i KAIA-kostnad-beregningen.
///
/// Verifiserer mot fasit-tabellen i <c>docs/SPEC-KAIA-KOSTNAD.md</c>:
///   - Jan 2026 (31 dager): 4000 × 31/365 = 339,7260...
///   - Feb 2026 (28 dager): 4000 × 28/365 = 306,8493...
///   - Mai 01.–17. (17 dager): 4000 × 17/365 = 186,3014...
/// </summary>
public class KaiaFeeProrationTests
{
    private static DateTimeOffset Utc(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    // --- Fasit-tabellen i specen --------------------------------------

    [Fact]
    public void Compute_HelJanuar_GirSpecFasit()
    {
        var start = Utc(2026, 1, 1);
        var end = Utc(2026, 2, 1);

        var result = KaiaFeeProration.Compute(4000, start, end);

        // Spec: 4000 × 31/365 = 339,7260...
        result.Should().BeApproximately(339.7260, 0.001);
    }

    [Fact]
    public void Compute_HelFebruar_GirSpecFasit()
    {
        var start = Utc(2026, 2, 1);
        var end = Utc(2026, 3, 1);

        var result = KaiaFeeProration.Compute(4000, start, end);

        // Spec: 4000 × 28/365 = 306,8493...
        result.Should().BeApproximately(306.8493, 0.001);
    }

    [Fact]
    public void Compute_Mai01til17_GirSpecFasit()
    {
        // Delvis mai-måned: 01.05 00:00 til 18.05 00:00 = 17 dager
        var start = Utc(2026, 5, 1);
        var end = Utc(2026, 5, 18);

        var result = KaiaFeeProration.Compute(4000, start, end);

        // Spec: 4000 × 17/365 = 186,3014...
        result.Should().BeApproximately(186.3014, 0.001);
    }

    // --- Tolv hele måneder skal summere til årsavgiften ----------------

    [Fact]
    public void Compute_TolvHeleMaaneder2026_SummererTilAarsavgift()
    {
        const double annual = 4000;
        var sum = 0.0;
        for (var m = 1; m <= 12; m++)
        {
            var start = Utc(2026, m, 1);
            var end = m == 12 ? Utc(2027, 1, 1) : Utc(2026, m + 1, 1);
            sum += KaiaFeeProration.Compute(annual, start, end);
        }

        // 2026 er ikke skuddår → 365 dager → eksakt 4000.
        sum.Should().BeApproximately(annual, 0.0001);
    }

    [Fact]
    public void Compute_TolvHeleMaaneder2024_Skuddaar_SummererTilAarsavgift()
    {
        const double annual = 4000;
        var sum = 0.0;
        for (var m = 1; m <= 12; m++)
        {
            var start = Utc(2024, m, 1);
            var end = m == 12 ? Utc(2025, 1, 1) : Utc(2024, m + 1, 1);
            sum += KaiaFeeProration.Compute(annual, start, end);
        }

        // 2024 er skuddår → 366 dager.
        sum.Should().BeApproximately(annual, 0.0001);
    }

    // --- DST-overgang skal ikke gi feil daystelling --------------------

    [Fact]
    public void Compute_HelMars2026_DstStart_GirNoyaktig31Dager()
    {
        // DST-start i Europe/Oslo er 29.03.2026 — TotalDays mellom UTC-tider
        // er likevel 31 fordi vi opererer i UTC. Sjekker at DST i lokaltid
        // ikke smitter inn via en eventuell konvertering.
        var start = Utc(2026, 3, 1);
        var end = Utc(2026, 4, 1);

        var result = KaiaFeeProration.Compute(4000, start, end);

        result.Should().BeApproximately(4000 * 31.0 / 365.0, 0.001);
    }

    [Fact]
    public void Compute_HelOktober2026_DstSlutt_GirNoyaktig31Dager()
    {
        // DST-slutt 25.10.2026 lokalt; samme resonnement som mars-testen.
        var start = Utc(2026, 10, 1);
        var end = Utc(2026, 11, 1);

        var result = KaiaFeeProration.Compute(4000, start, end);

        result.Should().BeApproximately(4000 * 31.0 / 365.0, 0.001);
    }

    // --- Edge cases ---------------------------------------------------

    [Fact]
    public void Compute_TomPeriode_GirNull()
    {
        var t = Utc(2026, 1, 1);
        KaiaFeeProration.Compute(4000, t, t).Should().Be(0);
    }

    [Fact]
    public void Compute_NegativPeriode_GirNull()
    {
        var start = Utc(2026, 2, 1);
        var end = Utc(2026, 1, 1);
        KaiaFeeProration.Compute(4000, start, end).Should().Be(0);
    }

    [Fact]
    public void Compute_NullAarsavgift_GirNull()
    {
        var start = Utc(2026, 1, 1);
        var end = Utc(2026, 2, 1);
        KaiaFeeProration.Compute(0, start, end).Should().Be(0);
    }

    [Fact]
    public void Compute_AnnenAarsavgift_SkalererProporsjonalt()
    {
        // 6000 × 31/365 vs 4000 × 31/365 → forholdet skal være 1,5
        var start = Utc(2026, 1, 1);
        var end = Utc(2026, 2, 1);

        var fourK = KaiaFeeProration.Compute(4000, start, end);
        var sixK = KaiaFeeProration.Compute(6000, start, end);

        (sixK / fourK).Should().BeApproximately(1.5, 0.0001);
    }

    // --- Vikeså januar-eksempel fra specen -----------------------------

    [Fact]
    public void Compute_VikesaaJanuar2026_GirFasit()
    {
        // Spec sier Vikeså jan 2026 = megler 495,10 + fast 339,73 = 834,83.
        // Verifiserer fast-komponenten her; meglerprovisjon verifiseres
        // i integrasjonstest av KaiaCostQueryService.
        var start = Utc(2026, 1, 1);
        var end = Utc(2026, 2, 1);
        var result = KaiaFeeProration.Compute(4000, start, end);

        result.Should().BeApproximately(339.73, 0.01);
    }
}
