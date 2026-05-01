using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// 13 tester for <see cref="HauklandSignalMapSeeder.MapTag"/> per
/// SPEC-KASKADE-DAMMER akseptansekriterium #11. Verifiserer auto-mapping
/// fra prefix/suffix til (rolle, damId, storeSamples).
///
/// Alle tester er rene funksjons-tester — ingen DB nødvendig.
/// </summary>
public class HauklandSignalMapSeederTests
{
    [Fact]
    public void MapTag_GeneratorActivePower_DamIdNull()
    {
        var (role, damId, store, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_G1_GEN_P_PV");
        role.Should().Be(SignalRole.GeneratorActivePower);
        damId.Should().BeNull();
        store.Should().BeTrue();
    }

    [Fact]
    public void MapTag_GeneratorRpm_DamIdNull()
    {
        var (role, damId, _, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_G1_GEN_TURTALL_PV");
        role.Should().Be(SignalRole.GeneratorRpm);
        damId.Should().BeNull();
    }

    [Fact]
    public void MapTag_TurbineWaterFlow_DamIdNull()
    {
        var (role, damId, _, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_G1_TURB_VF_PV");
        role.Should().Be(SignalRole.TurbineWaterFlow);
        damId.Should().BeNull();
    }

    [Fact]
    public void MapTag_StolsvtOverflow_DamIdStolsvt()
    {
        var (role, damId, store, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_STOLSVT_KONTROLL_MAG_OVLOP_PV");
        role.Should().Be(SignalRole.OverflowFlow);
        damId.Should().Be("haukland_stolsvt");
        store.Should().BeTrue();
    }

    [Fact]
    public void MapTag_GjelevtOverflow_DamIdGjelevt()
    {
        var (role, damId, _, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_GJELEVT_KONTROLL_MAG_OVLOP_PV");
        role.Should().Be(SignalRole.OverflowFlow);
        damId.Should().Be("haukland_gjelevt");
    }

    [Fact]
    public void MapTag_SkrstmvtOverflow_DamIdSkrstmvt()
    {
        var (role, damId, _, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_SKRSTMVT_KONTROLL_MAG_OVLOP_PV");
        role.Should().Be(SignalRole.OverflowFlow);
        damId.Should().Be("haukland_skrstmvt");
    }

    [Fact]
    public void MapTag_StemmevtOverflow_DamIdStemmevt_TerminalDam()
    {
        // Stemmevatn er terminal — denne tagen er den vakt-ROI-koden vil filtrere
        // til etter Steg 5 og bruke for overflow-detection.
        var (role, damId, store, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_STEMMEVT_KONTROLL_MAG_OVLOP_PV");
        role.Should().Be(SignalRole.OverflowFlow);
        damId.Should().Be("haukland_stemmevt");
        store.Should().BeTrue();
    }

    [Fact]
    public void MapTag_StolsvtGateFlow_RoleGateFlow()
    {
        // Stølsvatn har LUKE1 (luke til Stemmevatn). Stemmevatn mangler dette
        // bevisst (vannet går rett til turbin).
        var (role, damId, _, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_STOLSVT_LUKE1_VF_PV");
        role.Should().Be(SignalRole.GateFlow);
        damId.Should().Be("haukland_stolsvt");
    }

    [Fact]
    public void MapTag_StemmevtMangler_LUKE1_VF_TagFinnesIkkeISCADAEksport()
    {
        // Verifiserer at Stemmevatn IKKE har LUKE1_VF — vannet går rett til
        // turbin G1. Hvis tagen senere skulle dukke opp i CSV-eksport ville
        // den blitt mappet feilaktig her — men vår whitelist (HauklandTagCatalog)
        // inkluderer den ikke.
        HauklandTagCatalog.AllTags.Should().NotContain("HAUKLAND_STEMMEVT_LUKE1_VF_PV");
        HauklandTagCatalog.AllTags.Should().NotContain("HAUKLAND_STEMMEVT_LUKE1_POS_PV");
    }

    [Fact]
    public void MapTag_UkjentSuffix_RoleOtherStoreSamplesFalse()
    {
        // Lager-temp og hjelpekraft havner i Other med store_samples=false
        // — whitelistet men ikke aktivt brukt for KPI-er.
        var (role, _, store, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_STOLSVT_FORSYN_24VDC_BATT_TEMP_PV");
        role.Should().Be(SignalRole.Other);
        store.Should().BeFalse();
    }

    [Fact]
    public void MapTag_KrstKomAlarm_CommunicationAlarmDamIdNull()
    {
        var (role, damId, _, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_KRST_KONTROLL_KOM_AL");
        role.Should().Be(SignalRole.CommunicationAlarm);
        damId.Should().BeNull();
    }

    [Fact]
    public void MapTag_StemmevtKomAlarm_CommunicationAlarmDamIdStemmevt()
    {
        // Per-dam kom-alarm: tilkobles dammen, ikke null.
        var (role, damId, _, _) = HauklandSignalMapSeeder.MapTag("HAUKLAND_STEMMEVT_KONTROLL_KOM_AL");
        role.Should().Be(SignalRole.CommunicationAlarm);
        damId.Should().Be("haukland_stemmevt");
    }

    [Fact]
    public void MapTag_AlleTagsIKatalog_HarValidMapping()
    {
        // Idempotens-sjekk: ingen tag i katalogen kaster, alle gir gyldig
        // (rolle, damId, storeSamples, unit). Sikrer at fremtidige endringer
        // i MapTag ikke introduserer NullReferenceException eller liknende.
        // Sammen med hovedmapping-testene gir dette god dekning av alle 195 tags.
        foreach (var tag in HauklandTagCatalog.AllTags)
        {
            var act = () => HauklandSignalMapSeeder.MapTag(tag);
            act.Should().NotThrow($"tag '{tag}' skal ha en gyldig mapping");
        }

        HauklandTagCatalog.AllTags.Length.Should().Be(195);
    }

    [Fact]
    public void MapTag_HelhetligDamFordeling_4DammerFørNullSlutten()
    {
        // Verifiserer at de 4 dammene er distinkte og at antall tags-per-dam
        // matcher SCADA-eksporten:
        //   Stølsvatn:      33 tags
        //   Gjelevatn:      31 tags
        //   Skårstemmevatn: 31 tags
        //   Stemmevatn:     40 tags + 1 INNTAK-tag = 41 totalt
        //   Generator G1:   50 tags (DamId = null)
        //   Sentral/nett/etc: 60 - 50 - 41 = 9 tags resterende (null DamId)
        var byDam = HauklandTagCatalog.AllTags
            .Select(HauklandSignalMapSeeder.MapTag)
            .GroupBy(m => m.damId)
            .ToDictionary(g => g.Key ?? "(null)", g => g.Count());

        byDam.Should().ContainKey("haukland_stolsvt");
        byDam.Should().ContainKey("haukland_gjelevt");
        byDam.Should().ContainKey("haukland_skrstmvt");
        byDam.Should().ContainKey("haukland_stemmevt");
        byDam.Should().ContainKey("(null)");

        byDam["haukland_stolsvt"].Should().Be(33);
        byDam["haukland_gjelevt"].Should().Be(31);
        byDam["haukland_skrstmvt"].Should().Be(31);
        // Stemmevatn = 40 dam-tags + 1 INNTAK-tag (oppstrøms-koten til turbin)
        byDam["haukland_stemmevt"].Should().Be(41);
        // Resten: G1 (50) + KRST (2) + NETT (5) + VANNVEI (1) + V26 (1) = 59
        byDam["(null)"].Should().Be(59);
    }
}
