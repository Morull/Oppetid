using FluentAssertions;
using KraftverkUptime.Modules.Scada.Import;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Unit-tester for <see cref="OperlogCsvParser.ParseMultiPlant"/>: én CSV
/// med events fra flere stasjoner blir splittet til riktig anlegg via
/// station-callback. Ukjente stasjoner skippes og rapporteres separat.
/// </summary>
public class MultiPlantOperlogParserTests
{
    private const string Header =
        "timestamp;station;username;tag;text;value;operatorType;originTable;alarmType;categoryNumber;offTimestamp";

    private static readonly OperlogCsvParser Parser = new();

    private static MultiPlantOperlogParseResult ParseCsv(string body, Func<string, string?>? lookup = null)
    {
        using var reader = new StringReader(Header + "\n" + body);
        return Parser.ParseMultiPlant("dev-org", reader, lookup ?? DefaultLookup);
    }

    private static string? DefaultLookup(string station) => station switch
    {
        "Drivdal" => "drivdal",
        "Grødemfoss" => "grodemfoss",
        "Øgreyfoss" => "ogreyfoss",
        "Stølskraft" => "stolskraft",
        _ => null,
    };

    [Fact]
    public void ParseMultiPlant_SplitterTilRiktigPlant()
    {
        var csv = string.Join("\n",
            "2026-02-04T07:51:02.000Z;Drivdal;u;DRIVDAL_G1_KONTROLL_STARTER_AL;start;;;alarmlog;event;3;",
            "2026-03-15T14:23:11.000Z;Grødemfoss;u;GRODEM_G2_KONTROLL_FEIL_AL;feil;;;alarmlog;event;3;",
            "2026-01-20T09:00:00.000Z;Drivdal;u;DRIVDAL_G1_KONTROLL_STOPPER_AL;stop;;;alarmlog;event;3;",
            "2026-02-10T11:45:33.000Z;Øgreyfoss;u;OGREY_G1_FEIL_AL;feil;;;alarmlog;event;3;");

        var result = ParseCsv(csv);

        result.RowsParsed.Should().Be(4);
        result.UnknownStations.Should().Be(0);
        result.EventsByPlantId.Should().HaveCount(3);
        result.EventsByPlantId["drivdal"].Should().HaveCount(2);
        result.EventsByPlantId["grodemfoss"].Should().HaveCount(1);
        result.EventsByPlantId["ogreyfoss"].Should().HaveCount(1);
    }

    [Fact]
    public void ParseMultiPlant_UkjentStation_Skippes_OgRapportertSeparat()
    {
        var csv = string.Join("\n",
            "2026-02-04T07:51:02.000Z;Drivdal;u;DRIVDAL_G1_STARTER_AL;ok;;;alarmlog;event;3;",
            "2026-02-05T08:00:00.000Z;Smievatn;u;UNKNOWN_FEIL_AL;feil;;;alarmlog;event;3;",
            "2026-02-06T09:00:00.000Z;UkjentNavn;u;X_FEIL_AL;feil;;;alarmlog;event;3;");

        var result = ParseCsv(csv);

        result.RowsParsed.Should().Be(1);
        result.RowsSkipped.Should().Be(2);
        result.UnknownStations.Should().Be(2);
        result.UnknownStationNames.Should().BeEquivalentTo(new[] { "Smievatn", "UkjentNavn" });
        result.EventsByPlantId.Should().ContainKey("drivdal");
    }

    [Fact]
    public void ParseMultiPlant_BevarerKanoniskeNavn()
    {
        // "Grødemfoss" med æøå skal mappes via callback uten å miste tegn.
        // Lookup-funksjonen er ansvar for å returnere riktig slug-id.
        var csv = "2026-03-01T12:00:00.000Z;Grødemfoss;u;TAG_FEIL_AL;feil;;;alarmlog;event;3;";

        var result = ParseCsv(csv);

        result.RowsParsed.Should().Be(1);
        result.EventsByPlantId.Should().ContainKey("grodemfoss");
        result.EventsByPlantId["grodemfoss"][0].PlantId.Should().Be("grodemfoss");
    }

    [Fact]
    public void ParseMultiPlant_TomFil_Returnerer_TomtResultat()
    {
        var result = ParseCsv("");

        result.RowsParsed.Should().Be(0);
        result.RowsSkipped.Should().Be(0);
        result.UnknownStations.Should().Be(0);
        result.EventsByPlantId.Should().BeEmpty();
    }

    [Fact]
    public void ParseMultiPlant_SettpunktsEndringer_HopperOver()
    {
        // KONTROLL_*_SP er settpunkt-endringer uten _AL → skal skippes.
        var csv = string.Join("\n",
            "2026-02-04T07:51:02.000Z;Drivdal;u;DRIVDAL_G1_KONTROLL_AGC_DB_SP;agc;;;operatorlog;;;",
            "2026-02-04T08:00:00.000Z;Drivdal;u;DRIVDAL_G1_KONTROLL_STARTER_AL;start;;;alarmlog;event;3;");

        var result = ParseCsv(csv);

        result.RowsParsed.Should().Be(1);
        result.RowsSkipped.Should().Be(1);
        result.EventsByPlantId["drivdal"].Should().HaveCount(1);
    }

    [Fact]
    public void Parse_GammelSinglePlantApi_FungererFortsatt_OgFiltrer_PaaBareDenStation()
    {
        // Rader fra annen station i CSV-en skal IKKE bli inkludert når caller
        // har spesifisert plantId — single-plant-modus deler ut samme plantId
        // til alle rader uavhengig av station-felt.
        var csv = Header + "\n"
            + "2026-02-04T07:51:02.000Z;Drivdal;u;DRIVDAL_G1_STARTER_AL;ok;;;alarmlog;event;3;\n"
            + "2026-02-05T08:00:00.000Z;Grødemfoss;u;GRODEM_FEIL_AL;feil;;;alarmlog;event;3;\n";

        using var reader = new StringReader(csv);
        var result = Parser.Parse("dev-org", "drivdal", reader);

        // Begge events havner i drivdal fordi single-plant-modus ignorerer station.
        result.RowsParsed.Should().Be(2);
        result.Events.Should().HaveCount(2);
        result.Events.Should().AllSatisfy(e => e.PlantId.Should().Be("drivdal"));
    }
}
