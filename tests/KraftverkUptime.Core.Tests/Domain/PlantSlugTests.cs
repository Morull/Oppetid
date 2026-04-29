using FluentAssertions;
using KraftverkUptime.Core.Domain;
using Xunit;

namespace KraftverkUptime.Core.Tests.Domain;

/// <summary>
/// Verifiserer at <see cref="PlantSlug.ToSlug(string?)"/> produserer stabile,
/// ASCII-trygge plant-id-er. Endringer i denne tabellen er kontrakts-brytende
/// og endrer plant_id-er som allerede ligger i DB.
/// </summary>
public class PlantSlugTests
{
    [Theory]
    [InlineData("Løgjen", "logjen")]
    [InlineData("Drivdal", "drivdal")]
    [InlineData("Grødemfoss", "grodemfoss")]
    [InlineData("Haukland", "haukland")]
    [InlineData("Honnefoss", "honnefoss")]
    [InlineData("Lindland", "lindland")]
    [InlineData("Øgreyfoss", "ogreyfoss")]
    [InlineData("Ørsdalen", "orsdalen")]
    [InlineData("Liavatn", "liavatn")]
    [InlineData("Vikeså", "vikesa")]
    [InlineData("Stølskraft", "stolskraft")]
    public void ToSlug_KjenteAnlegg_GirForventetSlug(string navn, string expected)
    {
        PlantSlug.ToSlug(navn).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void ToSlug_TomtNavn_GirTomString(string? input)
    {
        PlantSlug.ToSlug(input).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Æra", "ara")]
    [InlineData("Åse", "ase")]
    [InlineData("ÆØÅæøå", "aoaaoa")]
    public void ToSlug_AlleNorskeBokstaver_HandteresSymmetrisk(string input, string expected)
    {
        PlantSlug.ToSlug(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Plant 2", "plant2")]
    [InlineData("Plant-A_b 2.0", "plantab20")]
    [InlineData("  Trim  ", "trim")]
    [InlineData("Lørenskog/Vikeså", "lorenskogvikesa")]
    public void ToSlug_FjernerSkilleTegn_OgTrimmerYtre(string input, string expected)
    {
        PlantSlug.ToSlug(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("DRIVDAL", "drivdal")]
    [InlineData("DrivDal", "drivdal")]
    public void ToSlug_ErCaseInsensitive(string input, string expected)
    {
        PlantSlug.ToSlug(input).Should().Be(expected);
    }

    [Fact]
    public void ToSlug_AlleredeSlug_ErIdempotent()
    {
        PlantSlug.ToSlug("logjen").Should().Be("logjen");
        PlantSlug.ToSlug(PlantSlug.ToSlug("Løgjen")).Should().Be("logjen");
    }
}
