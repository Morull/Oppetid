using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Scada.Import;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Verifiserer at <see cref="OperlogCsvParser.MapEvent"/> er anlegg-uavhengig:
/// suffix-matching (<c>_AL</c>, <c>STARTER_AL</c>, <c>STOPPER_AL</c>,
/// <c>FEIL_AL</c>) gjelder uavhengig av plant-prefiks.
///
/// Disse testene "låser" oppførselen når operlog-CSV fra andre anlegg lastes
/// opp (Vikeså, Stølskraft, Øgreyfoss osv.). Veikartets forventning:
/// MapEvent skal kun se på suffix, ikke prefiks.
/// </summary>
public class OperlogParserAnleggUavhengigTests
{
    private static readonly OperlogCsvParser Parser = new();

    private static OperlogParseResult ParseOne(string tag, string alarmType = "event")
    {
        var csv = "timestamp;station;username;tag;text;value;operatorType;originTable;alarmType;categoryNumber;offTimestamp\n"
            + $"2026-02-04T07:51:02.000Z;Test;Test;{tag};Test event;;;alarmlog;{alarmType};3;\n";
        using var reader = new StringReader(csv);
        return Parser.Parse("dev-org", "test-plant", reader);
    }

    [Theory]
    // Drifts-overganger (start/stop) — anlegg-prefiks varierer, suffix avgjør
    [InlineData("DRIVDAL_G1_KONTROLL_STARTER_AL", UnitState.InService, "operlog:start")]
    [InlineData("DRIVDAL_G1_KONTROLL_STOPPER_AL", UnitState.MaintenanceOutage, "operlog:stop")]
    [InlineData("LOGJEN_G1_KONTROLL_STARTER_AL", UnitState.InService, "operlog:start")]
    [InlineData("STOLSKRAFT_G1_STOPPER_AL", UnitState.MaintenanceOutage, "operlog:stop")]
    // Tekniske feil
    [InlineData("DRIVDAL_G1_KONTROLL_FEIL_AL", UnitState.ForcedOutage, "operlog:fault")]
    [InlineData("OGREYFOSS_G2_FEIL_AL", UnitState.ForcedOutage, "operlog:fault")]
    [InlineData("VIKESA_TURBIN_HAVARI_AL", UnitState.ForcedOutage, "operlog:fault")]
    [InlineData("LIAVATN_G1_TURB_FEIL_AL", UnitState.ForcedOutage, "operlog:turb-feil")]
    // Eksplisitte stopp-typer
    [InlineData("ORSDAL_G1_KONTROLL_NODSTOPP_AL", UnitState.ForcedOutage, "operlog:nodstopp")]
    [InlineData("ORSDAL_G1_KONTROLL_HURTIGSTOPP_AL", UnitState.ForcedOutage, "operlog:hurtigstopp")]
    [InlineData("ORSDAL_G1_KONTROLL_HURTIGSTOPP_MEK_AL", UnitState.ForcedOutage, "operlog:hurtigstopp")]
    // Rist-falltap (egen kategori for rist-detektor)
    [InlineData("DRIVDAL_INNTAK_RIST_FALLTAP_HH_AL", UnitState.ForcedOutage, "operlog:rist-falltap")]
    // Terskel-alarmer — ForcedOutage med lav confidence (FusionClassifier respekterer SCADA)
    [InlineData("HONNE_G1_GEN_P_LL_AL", UnitState.ForcedOutage, "operlog:lav-lav-alarm")]
    [InlineData("HAUKLAND_INNTAK_NIVA_OPPSTROM_HRV_HH_AL", UnitState.ForcedOutage, "operlog:hoy-hoy-alarm")]
    // Generisk _AL uten klar kategori — registreres som MaintenanceOutage (annoteres,
    // ikke automatisk forced outage) så de er sporbare uten å overdrive nedetid
    [InlineData("ORSDALEN_KOMM_LINJE_AL", UnitState.MaintenanceOutage, "operlog:annen-alarm")]
    public void MapEvent_AnleggUavhengig_BasertPaaSuffix(
        string tag, UnitState expectedState, string expectedCause)
    {
        var result = ParseOne(tag);

        result.RowsParsed.Should().Be(1);
        result.Events.Should().HaveCount(1);
        result.Events[0].State.Should().Be(expectedState);
        result.Events[0].CauseCode.Should().Be(expectedCause);
    }

    [Theory]
    // Bug fixet 2026-05-04: alarmType="alarm" med _AL-suffiks ble tidligere skippet
    // fordi MapEvent krevde alarmType="event". KraftScada bruker "alarm" for aktive
    // alarmer (med varighet via offTimestamp) — disse skal også fanges.
    [InlineData("HONNE_G1_GEN_P_LL_AL", "alarm", UnitState.ForcedOutage, "operlog:lav-lav-alarm")]
    [InlineData("LIAVATN_G1_KONTROLL_NODSTOPP_AL", "alarm", UnitState.ForcedOutage, "operlog:nodstopp")]
    [InlineData("LIAVATN_G1_TURB_FEIL_AL", "alarm", UnitState.ForcedOutage, "operlog:turb-feil")]
    public void MapEvent_AlarmType_FangerOgsaaAktive(
        string tag, string alarmType, UnitState expectedState, string expectedCause)
    {
        var result = ParseOne(tag, alarmType);

        result.Events.Should().HaveCount(1);
        result.Events[0].State.Should().Be(expectedState);
        result.Events[0].CauseCode.Should().Be(expectedCause);
    }

    [Fact]
    public void Settpunkt_Endring_HopperOver()
    {
        // KONTROLL_AGC_DB_SP er settpunkt-endring uten _AL-suffix → ingen state-endring,
        // skippes som row.
        var result = ParseOne("DRIVDAL_G1_KONTROLL_AGC_DB_SP", alarmType: "");

        result.RowsParsed.Should().Be(0);
        result.RowsSkipped.Should().Be(1);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public void EmptyCsv_BareHeader_ReturnererTomtResultat()
    {
        var csv = "timestamp;station;username;tag;text;value;operatorType;originTable;alarmType;categoryNumber;offTimestamp\n";
        using var reader = new StringReader(csv);
        var result = Parser.Parse("dev-org", "test-plant", reader);

        result.RowsParsed.Should().Be(0);
        result.Events.Should().BeEmpty();
    }
}
