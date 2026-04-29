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
    // Drivdal-format
    [InlineData("DRIVDAL_G1_KONTROLL_STARTER_AL", UnitState.InService, "operlog:start")]
    [InlineData("DRIVDAL_G1_KONTROLL_STOPPER_AL", UnitState.MaintenanceOutage, "operlog:stop")]
    [InlineData("DRIVDAL_G1_KONTROLL_FEIL_AL", UnitState.ForcedOutage, "operlog:fault")]
    // Andre anleggs-prefiks — samme suffix-er
    [InlineData("LOGJEN_G1_KONTROLL_STARTER_AL", UnitState.InService, "operlog:start")]
    [InlineData("OGREYFOSS_G2_FEIL_AL", UnitState.ForcedOutage, "operlog:fault")]
    [InlineData("STOLSKRAFT_G1_STOPPER_AL", UnitState.MaintenanceOutage, "operlog:stop")]
    [InlineData("VIKESA_TURBIN_HAVARI_AL", UnitState.ForcedOutage, "operlog:fault")]
    // Generisk alarm-event uten klar kategori — fall-through til ForcedOutage
    [InlineData("ORSDALEN_KOMM_LINJE_AL", UnitState.ForcedOutage, "operlog:alarm")]
    public void MapEvent_AnleggUavhengig_BasertPaaSuffix(
        string tag, UnitState expectedState, string expectedCause)
    {
        var result = ParseOne(tag);

        result.RowsParsed.Should().Be(1);
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
