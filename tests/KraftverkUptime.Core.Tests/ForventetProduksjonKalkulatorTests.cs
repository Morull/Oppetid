using FluentAssertions;
using KraftverkUptime.Core.Domain;
using Xunit;

namespace KraftverkUptime.Core.Tests;

/// <summary>
/// Fasit-tester mot driftsleders kraftverkoversikt (SPEC-MAANEDSPROFIL-
/// NORMALAAR §4/§8): Drivdal 8 055 MWh middelproduksjon × Dalane-profilen.
/// Perioder er [from, to) med dagbasert vekting — samme konvensjon som
/// dagens <c>NormalAarsAndel()</c> i Produksjon/Portefølje.
/// </summary>
public class ForventetProduksjonKalkulatorTests
{
    // Felles Dalane-profil fra arket (sum 100,0).
    private static readonly double[] DefaultProfil =
        [13.1, 11.0, 9.1, 8.1, 4.7, 2.3, 1.3, 4.9, 8.7, 10.4, 12.4, 14.0];

    private const double DrivdalGwh = 8.055; // 8 055 MWh middelproduksjon

    [Fact]
    public void Helaar_Med_Profil_Gir_Noyaktig_NormalGwh()
    {
        var mwh = ForventetProduksjonKalkulator.ForventetMwh(
            DrivdalGwh, DefaultProfil,
            new DateTime(2026, 1, 1), new DateTime(2027, 1, 1));

        // Sum av profilen er 100 % → hele normalåret.
        mwh.Should().NotBeNull();
        mwh!.Value.Should().BeApproximately(8055, 0.5);
    }

    [Fact]
    public void Januar_Alene_Gir_13_1_Prosent()
    {
        var mwh = ForventetProduksjonKalkulator.ForventetMwh(
            DrivdalGwh, DefaultProfil,
            new DateTime(2026, 1, 1), new DateTime(2026, 2, 1));

        // Arket: 8 055 × 13,1 % = 1 055 MWh (arket viser 1 056 — avrunding).
        mwh!.Value.Should().BeApproximately(8055 * 0.131, 0.5);
    }

    [Fact]
    public void Januar_Til_Mai_Gir_46_Prosent()
    {
        var mwh = ForventetProduksjonKalkulator.ForventetMwh(
            DrivdalGwh, DefaultProfil,
            new DateTime(2026, 1, 1), new DateTime(2026, 6, 1));

        // «Per Mai»: 13,1+11,0+9,1+8,1+4,7 = 46,0 % → 3 705 MWh (arket: 3 707).
        mwh!.Value.Should().BeApproximately(8055 * 0.460, 0.5);
    }

    [Fact]
    public void Delmaaned_Vektes_Dagbasert_Innenfor_Maaneden()
    {
        var mwh = ForventetProduksjonKalkulator.ForventetMwh(
            DrivdalGwh, DefaultProfil,
            new DateTime(2026, 5, 1), new DateTime(2026, 5, 15));

        // 1.–15. mai [from,to) = 14 dager av 31: 4,7 % × 14/31.
        mwh!.Value.Should().BeApproximately(8055 * 0.047 * 14.0 / 31.0, 0.5);
    }

    [Fact]
    public void Periode_Over_Aarsskiftet_Gjentar_Profilen()
    {
        var mwh = ForventetProduksjonKalkulator.ForventetMwh(
            DrivdalGwh, DefaultProfil,
            new DateTime(2026, 12, 1), new DateTime(2027, 2, 1));

        // Desember (14,0 %) + januar (13,1 %) = 27,1 %.
        mwh!.Value.Should().BeApproximately(8055 * 0.271, 0.5);
    }

    [Fact]
    public void Uten_Profil_Gir_Flat_ProRata_Inkl_Skuddaar()
    {
        // 2028 er skuddår → nevner 366.
        var mwh = ForventetProduksjonKalkulator.ForventetMwh(
            DrivdalGwh, profil: null,
            new DateTime(2028, 1, 1), new DateTime(2028, 2, 1));

        mwh!.Value.Should().BeApproximately(8.055 * 1000.0 * 31.0 / 366.0, 0.01);
    }

    [Fact]
    public void Mangler_NormalGwh_Eller_Tom_Periode_Gir_Null()
    {
        ForventetProduksjonKalkulator.ForventetMwh(
            null, DefaultProfil, new DateTime(2026, 1, 1), new DateTime(2026, 2, 1))
            .Should().BeNull();
        ForventetProduksjonKalkulator.ForventetMwh(
            0, DefaultProfil, new DateTime(2026, 1, 1), new DateTime(2026, 2, 1))
            .Should().BeNull();
        ForventetProduksjonKalkulator.ForventetMwh(
            DrivdalGwh, DefaultProfil, new DateTime(2026, 2, 1), new DateTime(2026, 2, 1))
            .Should().BeNull();
    }

    [Theory]
    [InlineData(11)]  // for få verdier
    [InlineData(13)]  // for mange
    public void Validering_Avviser_Feil_Antall_Verdier(int antall)
    {
        var profil = Enumerable.Repeat(100.0 / antall, antall).ToArray();
        ForventetProduksjonKalkulator.ValiderProfil(profil).Should().NotBeNull();
    }

    [Fact]
    public void Validering_Avviser_Negative_Verdier_Og_Feil_Sum()
    {
        var negativ = (double[])DefaultProfil.Clone();
        negativ[3] = -1;
        ForventetProduksjonKalkulator.ValiderProfil(negativ).Should().NotBeNull();

        var feilSum = (double[])DefaultProfil.Clone();
        feilSum[0] += 5; // sum 105
        ForventetProduksjonKalkulator.ValiderProfil(feilSum).Should().NotBeNull();
    }

    [Fact]
    public void Validering_Godtar_Null_Og_Gyldig_Profil()
    {
        ForventetProduksjonKalkulator.ValiderProfil(null).Should().BeNull();
        ForventetProduksjonKalkulator.ValiderProfil(DefaultProfil).Should().BeNull();
    }
}
