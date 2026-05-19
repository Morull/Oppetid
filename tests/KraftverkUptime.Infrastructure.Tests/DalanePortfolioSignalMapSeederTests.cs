using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// Tester for <see cref="DalanePortfolioSignalMapSeeder.MapTag"/> — verifiserer
/// at hver SCADA-tag fra master-eksporten 2026-05-18 (72 tags) havner på
/// riktig (plant_id, rolle, damId).
///
/// MapTag testes som pure-funksjon (ingen DB). Idempotens / dam-håndtering
/// dekkes av eksisterende infrastruktur-tester for andre seeders.
/// </summary>
public sealed class DalanePortfolioSignalMapSeederTests
{
    [Theory]
    // VIKESÅ — generator G1 (damId=null)
    [InlineData("VIKESA_G1_GEN_P_PV", "vikesa", SignalRole.GeneratorActivePower, null)]
    [InlineData("VIKESA_G1_GEN_TURTALL_PV", "vikesa", SignalRole.GeneratorRpm, null)]
    [InlineData("VIKESA_G1_GEN_F_PV", "vikesa", SignalRole.GeneratorFrequency, null)]
    [InlineData("VIKESA_G1_GEN_COSPHI_PV", "vikesa", SignalRole.ElectricalMeasurement, null)]
    [InlineData("VIKESA_G1_TURB_VF_PV", "vikesa", SignalRole.TurbineWaterFlow, null)]
    [InlineData("VIKESA_G1_TURB_VIRKNGRD_PV", "vikesa", SignalRole.TurbineEfficiency, null)]
    [InlineData("VIKESA_G1_TURB_LEDEAPP_POS_PV", "vikesa", SignalRole.GuideVanePosition, null)]
    [InlineData("VIKESA_G1_TURB_VANN_TRYKK_PV", "vikesa", SignalRole.HydraulicPressure, null)]
    [InlineData("VIKESA_G1_RORGATE_VANN_TRYKK_PV", "vikesa", SignalRole.HydraulicPressure, null)]
    [InlineData("VIKESA_G1_HYDRL_OLJE_TRYKK_PV", "vikesa", SignalRole.HydraulicPressure, null)]
    [InlineData("VIKESA_KRST_KONTROLL_KOM_AL", "vikesa", SignalRole.CommunicationAlarm, null)]
    // VIKESÅ — INNTAK (festes til vikesa_main)
    [InlineData("VIKESA_INNTAK_NIVA_OVERLOP_VF_PV", "vikesa", SignalRole.OverflowFlow, "vikesa_main")]
    [InlineData("VIKESA_INNTAK_MAGASIN_FYLLGRAD_PV", "vikesa", SignalRole.ReservoirFillFactor, "vikesa_main")]
    [InlineData("VIKESA_INNTAK_MAGASIN_VOLUM_PV", "vikesa", SignalRole.ReservoirVolume, "vikesa_main")]
    [InlineData("VIKESA_INNTAK_MAGASIN_NED_KAP_PV", "vikesa", SignalRole.LowestRegulatedLevel, "vikesa_main")]
    // STOLSKRAFT
    [InlineData("STOLSKRAFT_G1_GEN_P_PV", "stolskraft", SignalRole.GeneratorActivePower, null)]
    [InlineData("STOLSKRAFT_G1_GEN_TURTALL_PV", "stolskraft", SignalRole.GeneratorRpm, null)]
    [InlineData("STOLSKRAFT_G1_TURB_VF_PV", "stolskraft", SignalRole.TurbineWaterFlow, null)]
    // ØRSDALEN (prefix ORSDAL ≠ slug orsdalen)
    [InlineData("ORSDAL_G1_GEN_P_PV", "orsdalen", SignalRole.GeneratorActivePower, null)]
    [InlineData("ORSDAL_G1_TURB_PADRAG_PV", "orsdalen", SignalRole.TurbinePadrag, null)]
    [InlineData("ORSDAL_G1_TURB_VANN_TRYKK_PV", "orsdalen", SignalRole.HydraulicPressure, null)]
    [InlineData("ORSDAL_KRST_KONTROLL_KOM_AL", "orsdalen", SignalRole.CommunicationAlarm, null)]
    [InlineData("ORSDAL_INNTAK_NIVA_OPPSTROM_KOTE_PV", "orsdalen", SignalRole.UpstreamLevel, "orsdalen_main")]
    public void MapTag_GeneratorAndDamTags_FaarRiktigPlantOgRolle(
        string tagId, string expectedPlantId, SignalRole expectedRole, string? expectedDamId)
    {
        var (plantId, role, damId, _) = DalanePortfolioSignalMapSeeder.MapTag(tagId);
        plantId.Should().Be(expectedPlantId);
        role.Should().Be(expectedRole);
        damId.Should().Be(expectedDamId);
    }

    [Theory]
    // ØGREYFOSS — OGREY1-prefix (G1-side med INNTAK)
    [InlineData("OGREY1_G1_GEN_P_PV", SignalRole.GeneratorActivePower, null)]
    [InlineData("OGREY1_G1_GEN_TURTALL_PV", SignalRole.GeneratorRpm, null)]
    [InlineData("OGREY1_G1_GEN_F_PV", SignalRole.GeneratorFrequency, null)]
    [InlineData("OGREY1_G1_GEN_COSPHI_PV", SignalRole.ElectricalMeasurement, null)]
    [InlineData("OGREY1_G1_TURB_VF_PV", SignalRole.TurbineWaterFlow, null)]
    [InlineData("OGREY1_G1_TURB_VIRKNGRD_PV", SignalRole.TurbineEfficiency, null)]
    [InlineData("OGREY1_KRST_KONTROLL_KOM_AL", SignalRole.CommunicationAlarm, null)]
    [InlineData("OGREY1_G1_KONTROLL_REG_P_SP_SP_LAST", SignalRole.Other, null)]
    // ØGREYFOSS — INNTAK (felles mellom G1 og G2)
    [InlineData("OGREY1_INNTAK_NIVA_OVERLOP_VF_PV", SignalRole.OverflowFlow, "ogreyfoss_main")]
    [InlineData("OGREY1_INNTAK_MAGASIN_FYLLGRAD_PV", SignalRole.ReservoirFillFactor, "ogreyfoss_main")]
    [InlineData("OGREY1_INNTAK_MAGASIN_VOLUM_PV", SignalRole.ReservoirVolume, "ogreyfoss_main")]
    [InlineData("OGREY1_INNTAK_MAGASIN_TOT_VF_PV", SignalRole.TotalDamFlow, "ogreyfoss_main")]
    [InlineData("OGREY1_INNTAK_NIVA_OPPSTROM_KOTE_PV", SignalRole.UpstreamLevel, "ogreyfoss_main")]
    [InlineData("OGREY1_INNTAK_NIVA_NEDSTROM_REF_HRV_PV", SignalRole.Other, null)]
    [InlineData("OGREY1_INNTAK_NIVA_VTA_PV", SignalRole.Other, null)]
    // ØGREYFOSS — OGREY2-prefix (G2-side, deler INNTAK med OGREY1)
    [InlineData("OGREY2_G2_GEN_P_PV", SignalRole.GeneratorActivePower, null)]
    [InlineData("OGREY2_G2_GEN_TURTALL_PV", SignalRole.GeneratorRpm, null)]
    [InlineData("OGREY2_G2_TURB_VF_PV", SignalRole.TurbineWaterFlow, null)]
    [InlineData("OGREY2_G2_TURB_VIRKNGRD_PV", SignalRole.TurbineEfficiency, null)]
    [InlineData("OGREY2_KRST_KONTROLL_KOM_AL", SignalRole.CommunicationAlarm, null)]
    public void MapTag_Ogreyfoss_BeggePrefikser_GirOgreyfossPlantId(
        string tagId, SignalRole expectedRole, string? expectedDamId)
    {
        var (plantId, role, damId, _) = DalanePortfolioSignalMapSeeder.MapTag(tagId);
        plantId.Should().Be("ogreyfoss", "begge OGREY1/OGREY2 prefikser mapper til samme plant_id");
        role.Should().Be(expectedRole);
        damId.Should().Be(expectedDamId);
    }

    [Theory]
    // LØGJEN — generator G1
    [InlineData("LOGJEN_G1_GEN_P_PV", SignalRole.GeneratorActivePower, null)]
    [InlineData("LOGJEN_G1_GEN_F_PV", SignalRole.GeneratorFrequency, null)]
    [InlineData("LOGJEN_G1_GEN_COSPHI_PV", SignalRole.ElectricalMeasurement, null)]
    [InlineData("LOGJEN_G1_TURB_VF_PV", SignalRole.TurbineWaterFlow, null)]
    [InlineData("LOGJEN_G1_TURB_VIRKNGRD_PV", SignalRole.TurbineEfficiency, null)]
    [InlineData("LOGJEN_G1_TURB_LEDEAPP_POS_PV", SignalRole.GuideVanePosition, null)]
    [InlineData("LOGJEN_KRST_KONTROLL_KOM_AL", SignalRole.CommunicationAlarm, null)]
    // LØGJEN — INNTAK
    [InlineData("LOGJEN_INNTAK_NIVA_OVERLOP_VF_PV", SignalRole.OverflowFlow, "logjen_main")]
    [InlineData("LOGJEN_INNTAK_MAGASIN_FYLLGRAD_PV", SignalRole.ReservoirFillFactor, "logjen_main")]
    [InlineData("LOGJEN_INNTAK_MAGASIN_VOLUM_PV", SignalRole.ReservoirVolume, "logjen_main")]
    [InlineData("LOGJEN_INNTAK_MAGASIN_TOT_VF_PV", SignalRole.TotalDamFlow, "logjen_main")]
    public void MapTag_Logjen_FaarLogjenPlantOgRiktigDamId(
        string tagId, SignalRole expectedRole, string? expectedDamId)
    {
        var (plantId, role, damId, _) = DalanePortfolioSignalMapSeeder.MapTag(tagId);
        plantId.Should().Be("logjen");
        role.Should().Be(expectedRole);
        damId.Should().Be(expectedDamId);
    }

    [Fact]
    public void MapTag_Logjen_PRST_PV_Variant_BlirGeneratorRpm()
    {
        // LOGJEN bruker en SCADA-variant med _PRST_PV-suffix istedenfor _PV.
        // Verifiser at den klassifiseres som GeneratorRpm (samme som _PV-variant).
        var (plantId, role, damId, unit) = DalanePortfolioSignalMapSeeder.MapTag("LOGJEN_G1_GEN_TURTALL_PRST_PV");
        plantId.Should().Be("logjen");
        role.Should().Be(SignalRole.GeneratorRpm);
        damId.Should().BeNull();
        unit.Should().Be("rpm");
    }

    [Fact]
    public void MapTag_UkjentPrefix_FaarTomPlantId_OgOther()
    {
        var (plantId, role, damId, _) = DalanePortfolioSignalMapSeeder.MapTag("UKJENT_ANLEGG_NOE_TAG");
        plantId.Should().BeEmpty();
        role.Should().Be(SignalRole.Other);
        damId.Should().BeNull();
    }

    [Fact]
    public void DalanePortfolio72TagCatalog_HarEksakt_72_Unike_Tags()
    {
        DalanePortfolio72TagCatalog.Tags.Should().HaveCount(72);
        DalanePortfolio72TagCatalog.Tags.Distinct().Should().HaveCount(72, "ingen duplikater");
    }

    [Fact]
    public void MapTag_AlleTagsIKatalogen_FaarEnGyldigPlantId()
    {
        // Sanity: ingen tag i 72-katalogen returnerer tom plant_id (alle skal
        // matche et av de 6 prefiksene VIKESA/STOLSKRAFT/ORSDAL/OGREY1/OGREY2/LOGJEN).
        var validPlants = new HashSet<string>(StringComparer.Ordinal)
        {
            "vikesa", "stolskraft", "orsdalen", "ogreyfoss", "logjen",
        };

        foreach (var tag in DalanePortfolio72TagCatalog.Tags)
        {
            var (plantId, _, _, _) = DalanePortfolioSignalMapSeeder.MapTag(tag);
            validPlants.Should().Contain(plantId,
                $"tag {tag} skal mappes til et av de 5 anleggene");
        }
    }

    [Fact]
    public void MapTag_OverflowTags_ErTilgjengeligFor3Anlegg()
    {
        // Vakt-ROI krever overflow-tag. Av de 5 nye anleggene har 3 dette:
        // Vikeså, Øgreyfoss (via OGREY1) og Løgjen. Stølskraft og Ørsdalen mangler.
        var overflowTags = DalanePortfolio72TagCatalog.Tags
            .Where(t => t.EndsWith("_NIVA_OVERLOP_VF_PV", StringComparison.Ordinal))
            .Select(t => DalanePortfolioSignalMapSeeder.MapTag(t).PlantId)
            .ToHashSet(StringComparer.Ordinal);

        overflowTags.Should().BeEquivalentTo(new[] { "vikesa", "ogreyfoss", "logjen" });
    }

    [Fact]
    public void MapTag_TagFordeling_MatcherForventetPerAnlegg()
    {
        // Dokumenterer faktisk tag-fordeling. Hvis denne testen feiler,
        // har noen lagt til/fjernet tags i katalogen — oppdater forventningene
        // sammen med endringen.
        var perPlant = DalanePortfolio72TagCatalog.Tags
            .GroupBy(t => DalanePortfolioSignalMapSeeder.MapTag(t).PlantId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        perPlant.Should().HaveCount(5);
        perPlant["vikesa"].Should().Be(17);
        perPlant["stolskraft"].Should().Be(3, "Stølskraft har minimal SCADA-dekning");
        perPlant["orsdalen"].Should().Be(5, "Ørsdalen mangler TURB_VF og overflow");
        perPlant["ogreyfoss"].Should().Be(31, "Øgreyfoss = OGREY1 (20) + OGREY2 (11)");
        perPlant["logjen"].Should().Be(16);
    }
}
