using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

/// <summary>
/// Tester for <see cref="LindlandSignalMapSeeder.MapTag"/> — verifiserer at
/// hver SCADA-tag-kategori havner på riktig (rolle, dam) etter spec'en
/// (HEIGRAVT/EIAVT/BARSTDVT/INNTAK + G1/G2/NETT/KRST).
///
/// Vi tester MapTag i isolasjon (pure funksjon, ingen DB) — DB-integrasjonen
/// er allerede dekket av eksisterende DataCompletenessQueryServiceTests-mønstret.
/// </summary>
public sealed class LindlandSignalMapSeederTests
{
    [Theory]
    // Generator G1 — alle tags har damId=null
    [InlineData("LINDLAND_G1_GEN_P_PV", SignalRole.GeneratorActivePower, null)]
    [InlineData("LINDLAND_G1_GEN_TURTALL_PV", SignalRole.GeneratorRpm, null)]
    [InlineData("LINDLAND_G1_GEN_F_PV", SignalRole.GeneratorFrequency, null)]
    [InlineData("LINDLAND_G1_GEN_COSPHI_PV", SignalRole.ElectricalMeasurement, null)]
    [InlineData("LINDLAND_G1_TURB_VF_PV", SignalRole.TurbineWaterFlow, null)]
    [InlineData("LINDLAND_G1_TURB_VIRKNGRD_PV", SignalRole.TurbineEfficiency, null)]
    [InlineData("LINDLAND_G1_TURB_LEDEAPP_POS_PV", SignalRole.GuideVanePosition, null)]
    [InlineData("LINDLAND_G1_RORGATE_VANN_TRYKK_PV", SignalRole.HydraulicPressure, null)]
    [InlineData("LINDLAND_G1_KONTROLL_KOM_AL", SignalRole.CommunicationAlarm, null)]
    [InlineData("LINDLAND_G1_GEN_RADLAGER_DE_TEMP_PV", SignalRole.ConditionTemperature, null)]
    // Generator G2 — alle tags har damId=null
    [InlineData("LINDLAND_G2_GEN_P_PV", SignalRole.GeneratorActivePower, null)]
    [InlineData("LINDLAND_G2_TURB_VF_PV", SignalRole.TurbineWaterFlow, null)]
    [InlineData("LINDLAND_G2_KONTROLL_KOM_AL", SignalRole.CommunicationAlarm, null)]
    public void MapTag_GeneratorTag_DamIdErNull(string tagId, SignalRole expectedRole, string? expectedDamId)
    {
        var (role, damId) = LindlandSignalMapSeeder.MapTag(tagId);
        role.Should().Be(expectedRole);
        damId.Should().Be(expectedDamId);
    }

    [Theory]
    // HEIGRAVT (posisjon 1, regulert)
    [InlineData("LINDLAND_HEIGRAVT_NIVA_SENSOR_PRI_KOTE_PV", SignalRole.UpstreamLevel, "lindland_heigravatn")]
    [InlineData("LINDLAND_HEIGRAVT_KONTROLL_MAG_FYLLGRD_PV", SignalRole.ReservoirFillFactor, "lindland_heigravatn")]
    [InlineData("LINDLAND_HEIGRAVT_KONTROLL_MAG_VOLUM_PV", SignalRole.ReservoirVolume, "lindland_heigravatn")]
    [InlineData("LINDLAND_HEIGRAVT_KONTROLL_MAG_OVLOP_PV", SignalRole.OverflowFlow, "lindland_heigravatn")]
    [InlineData("LINDLAND_HEIGRAVT_LUKE1_VF_PV", SignalRole.GateFlow, "lindland_heigravatn")]
    [InlineData("LINDLAND_HEIGRAVT_LUKE1_POS_PV", SignalRole.GatePosition, "lindland_heigravatn")]
    [InlineData("LINDLAND_HEIGRAVT_KONTROLL_KOM_AL", SignalRole.CommunicationAlarm, "lindland_heigravatn")]
    // EIAVT (posisjon 2, regulert)
    [InlineData("LINDLAND_EIAVT_KONTROLL_MAG_FYLLGRD_PV", SignalRole.ReservoirFillFactor, "lindland_eiavatn")]
    [InlineData("LINDLAND_EIAVT_KONTROLL_MAG_OVLOP_PV", SignalRole.OverflowFlow, "lindland_eiavatn")]
    [InlineData("LINDLAND_EIAVT_LUKE1_VF_PV", SignalRole.GateFlow, "lindland_eiavatn")]
    // BARSTDVT (posisjon 3, uregulert)
    [InlineData("LINDLAND_BARSTDVT_NIVA_SENSOR_PRI_KOTE_PV", SignalRole.UpstreamLevel, "lindland_barstadvatn")]
    [InlineData("LINDLAND_BARSTDVT_KONTROLL_TOT_VF_PV", SignalRole.TotalDamFlow, "lindland_barstadvatn")]
    // INNTAK (Rosslandshølen, terminal-dam)
    [InlineData("LINDLAND_INNTAK_NIVA_OVERLOP_VF_PV", SignalRole.OverflowFlow, "lindland_rosslandshølen")]
    [InlineData("LINDLAND_INNTAK_KONTROLL_MAG_OVLOP_PV", SignalRole.OverflowFlow, "lindland_rosslandshølen")]
    [InlineData("LINDLAND_INNTAK_KONTROLL_MAG_FYLLGRD_PV", SignalRole.ReservoirFillFactor, "lindland_rosslandshølen")]
    [InlineData("LINDLAND_INNTAK_KONTROLL_TOT_VF_PV", SignalRole.TotalDamFlow, "lindland_rosslandshølen")]
    [InlineData("LINDLAND_INNTAK_NIVA_OPPSTROM_KOTE_PV", SignalRole.UpstreamLevel, "lindland_rosslandshølen")]
    [InlineData("LINDLAND_INNTAK_NIVA_NEDSTROM_KOTE_PV", SignalRole.DownstreamLevel, "lindland_rosslandshølen")]
    public void MapTag_DamTag_FaarRiktigDamId(string tagId, SignalRole expectedRole, string expectedDamId)
    {
        var (role, damId) = LindlandSignalMapSeeder.MapTag(tagId);
        role.Should().Be(expectedRole);
        damId.Should().Be(expectedDamId);
    }

    [Theory]
    // KRST = sensor-stasjon på utløp, ikke dam
    [InlineData("LINDLAND_KRST_KONTROLL_KOM_AL", SignalRole.CommunicationAlarm, null)]
    [InlineData("LINDLAND_KRST_NIVA_UTLOP_PV", SignalRole.Other, null)]
    // NETT = elektrisk avgangsside
    [InlineData("LINDLAND_NETT_LINJE_FASE_L1_U_L1_L2_PV", SignalRole.ElectricalMeasurement, null)]
    public void MapTag_NonDamTags_DamIdErNull(string tagId, SignalRole expectedRole, string? expectedDamId)
    {
        var (role, damId) = LindlandSignalMapSeeder.MapTag(tagId);
        role.Should().Be(expectedRole);
        damId.Should().Be(expectedDamId);
    }

    [Fact]
    public void Lindland117TagCatalog_HarEksakt_117_Unike_Tags()
    {
        Lindland117TagCatalog.Tags.Should().HaveCount(117);
        Lindland117TagCatalog.Tags.Distinct().Should().HaveCount(117, "ingen duplikater");
    }

    [Fact]
    public void MapTag_AlleTagsIKatalogen_FaarEnRolle()
    {
        // Sanity: ingen tag i 117-katalogen returnerer null fra MapTag.
        // (MapTag returnerer alltid en role; bare damId kan være null for
        //  generator/sensor-tags.)
        foreach (var tag in Lindland117TagCatalog.Tags)
        {
            var (role, _) = LindlandSignalMapSeeder.MapTag(tag);
            role.ToString().Should().NotBeEmpty(
                $"tag {tag} skal ha en gyldig rolle (selv om Other er fallback)");
        }
    }

    [Fact]
    public void MapTag_TerminalOverloop_ErRosslandshølen()
    {
        // Vakt-ROI plukker overflow fra terminal-dam (IsTurbineIntake=true).
        // Verifiser at INNTAK_NIVA_OVERLOP_VF_PV mappes til rosslandshølen,
        // siden det er kontinuerlig vannførings-måling som spec'en peker på.
        var (role, damId) = LindlandSignalMapSeeder.MapTag("LINDLAND_INNTAK_NIVA_OVERLOP_VF_PV");
        role.Should().Be(SignalRole.OverflowFlow);
        damId.Should().Be("lindland_rosslandshølen");
    }

    [Fact]
    public void MapTag_UkjentPrefix_FaarOther_OgNullDamId()
    {
        var (role, damId) = LindlandSignalMapSeeder.MapTag("LINDLAND_UKJENTSITE_NOE_RANDOM");
        role.Should().Be(SignalRole.Other);
        damId.Should().BeNull();
    }
}
