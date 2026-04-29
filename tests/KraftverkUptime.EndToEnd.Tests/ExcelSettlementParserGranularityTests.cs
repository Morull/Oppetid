using ClosedXML.Excel;
using FluentAssertions;
using KraftverkUptime.Modules.Settlement.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// End-to-end-tester for granularity-deteksjonen i <see cref="ExcelSettlementParser"/>.
/// Genererer syntetiske portaleksport-XLSX-er i minne (60-min og 15-min) og
/// verifiserer at:
///   1) 60-min-output passerer uendret igjennom parseren
///   2) 15-min-output aggregeres til time-rader med korrekt sum og vektet snitt
///   3) Begge ender opp med samme schema og kolonner
/// </summary>
public class ExcelSettlementParserGranularityTests
{
    private static ExcelSettlementParser CreateParser() =>
        new(new SettlementSchemaRegistry(), NullLogger<ExcelSettlementParser>.Instance);

    [Fact]
    public async Task Parse_SyntheticHourlyExport_ProducesOneRowPerHour()
    {
        var stream = BuildSyntheticPortalExport(intervalMinutes: 60, hours: 24);
        var parser = CreateParser();
        var parsed = await parser.ParseAsync(stream, default);

        parsed.Hourly.Should().HaveCount(24);
        parsed.SchemaVersion.Should().Be("portal-v1");
        // Første time = 00:00 lokal CET = 23:00 forrige dag UTC
        parsed.Hourly[0].MwhElhub.Should().BeApproximately(1.0, 1e-9);
        parsed.Hourly[0].SpotbudMwh.Should().BeApproximately(2.0, 1e-9);
        parsed.Hourly[0].SpotprisNokMwh.Should().BeApproximately(500, 1e-9);
    }

    [Fact]
    public async Task Parse_Synthetic15MinExport_AggregeresTilSammeAntallTimerSomHourly()
    {
        // Samme dataform som hourly-testen, men 4 rader per time. Aggregeringen
        // skal gi nøyaktig samme antall rader, og samme totale verdier per time.
        var stream = BuildSyntheticPortalExport(intervalMinutes: 15, hours: 24);
        var parser = CreateParser();
        var parsed = await parser.ParseAsync(stream, default);

        parsed.Hourly.Should().HaveCount(24);
        parsed.SchemaVersion.Should().Be("portal-v1");

        // Per time: 4 × 0.25 = 1.0 MWh elhub; 4 × 0.5 = 2.0 MWh spotbud
        parsed.Hourly[0].MwhElhub.Should().BeApproximately(1.0, 1e-9);
        parsed.Hourly[0].SpotbudMwh.Should().BeApproximately(2.0, 1e-9);

        // Spotpris er samme i alle kvartal (500) → vektet snitt = 500
        parsed.Hourly[0].SpotprisNokMwh.Should().BeApproximately(500, 1e-9);
    }

    [Fact]
    public async Task Parse_Synthetic15Min_OgHourly_GirIdentiskeResultater()
    {
        // Når begge eksporter har samme totale energi/marked per time,
        // skal output være numerisk likt — det er hele poenget med aggregeringen.
        var hourlyStream = BuildSyntheticPortalExport(intervalMinutes: 60, hours: 12);
        var quarterStream = BuildSyntheticPortalExport(intervalMinutes: 15, hours: 12);

        var parser = CreateParser();
        var fromHourly = await parser.ParseAsync(hourlyStream, default);
        var fromQuarter = await parser.ParseAsync(quarterStream, default);

        fromQuarter.Hourly.Should().HaveCount(fromHourly.Hourly.Count);

        for (var i = 0; i < fromHourly.Hourly.Count; i++)
        {
            var h = fromHourly.Hourly[i];
            var q = fromQuarter.Hourly[i];

            q.TimeUtc.Should().Be(h.TimeUtc, $"row {i}");
            q.MwhElhub.Should().BeApproximately(h.MwhElhub!.Value, 1e-9, $"row {i} MwhElhub");
            q.SpotbudMwh.Should().BeApproximately(h.SpotbudMwh!.Value, 1e-9, $"row {i} SpotbudMwh");
            q.SpotprisNokMwh.Should().BeApproximately(h.SpotprisNokMwh!.Value, 1e-9, $"row {i} SpotprisNokMwh");
            q.OppgjorNok.Should().BeApproximately(h.OppgjorNok!.Value, 1e-9, $"row {i} OppgjorNok");
        }
    }

    /// <summary>
    /// Bygger en minimal portaleksport-XLSX (Summering-fane + verk-fane) med
    /// gitt tidsoppløsning. Brukes som syntetisk fixture i testene.
    /// </summary>
    private static MemoryStream BuildSyntheticPortalExport(int intervalMinutes, int hours)
    {
        var quartersPerHour = 60 / intervalMinutes;
        var rowCount = hours * quartersPerHour;

        // Per-rad-verdier velges slik at aggregeringen til hourly er deterministisk:
        // hver kvartal (eller hele time) bidrar med følgende, og summen per time
        // skal gi samme tall i begge eksport-formater.
        //   MwhElhub:        per_rad = 1.0 / quartersPerHour      → time-sum = 1.0
        //   MwhESett:        per_rad = 1.0 / quartersPerHour      → time-sum = 1.0
        //   SpotbudMwh:      per_rad = 2.0 / quartersPerHour      → time-sum = 2.0
        //   SpotprisNokMwh:  per_rad = 500 (konstant)             → time-snitt = 500
        //   OppgjorNok:      per_rad = 100 / quartersPerHour      → time-sum = 100
        var perRowMwh = 1.0 / quartersPerHour;
        var perRowBud = 2.0 / quartersPerHour;
        var perRowOppgjor = 100.0 / quartersPerHour;

        using var workbook = new XLWorkbook();

        // --- Summering-fane ---
        var summary = workbook.Worksheets.Add("Summering");
        summary.Cell(1, 1).Value = "Tidsserie";
        summary.Cell(1, 2).Value = "MWh-Elhub";
        summary.Cell(1, 3).Value = "MWh-eSett";
        summary.Cell(1, 4).Value = "Spotbud";
        summary.Cell(1, 5).Value = "Oppgjør";
        summary.Cell(2, 1).Value = "Hele perioden";
        summary.Cell(2, 2).Value = hours * 1.0;
        summary.Cell(2, 3).Value = hours * 1.0;
        summary.Cell(2, 4).Value = hours * 2.0;
        summary.Cell(2, 5).Value = hours * 100.0;

        // --- Verk-fane ---
        var plant = workbook.Worksheets.Add("1 Synthetic");
        plant.Cell(1, 1).Value = "Synthetic 01.02.2025 - 02.02.2025";

        // Header-rad (rad 2) — alle V1-required-kolonner + de vi tester
        plant.Cell(2, 1).Value = "Time";
        plant.Cell(2, 2).Value = "MWh-Elhub";
        plant.Cell(2, 3).Value = "MWh-eSett";
        plant.Cell(2, 4).Value = "Spotbud";
        plant.Cell(2, 5).Value = "Spotpris";
        plant.Cell(2, 6).Value = "Ubalanse";
        plant.Cell(2, 7).Value = "Oppgjør";

        // Enhetsrad (rad 3) — skippes av parseren
        plant.Cell(3, 1).Value = "";
        plant.Cell(3, 2).Value = "MWh";
        plant.Cell(3, 3).Value = "MWh";
        plant.Cell(3, 4).Value = "MWh";
        plant.Cell(3, 5).Value = "NOK/MWh";
        plant.Cell(3, 6).Value = "MWh";
        plant.Cell(3, 7).Value = "NOK";

        // Data fra rad 4
        var startLocal = new DateTime(2025, 2, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var rowNumber = 4;
        for (var i = 0; i < rowCount; i++)
        {
            var ts = startLocal.AddMinutes(i * intervalMinutes);
            plant.Cell(rowNumber, 1).Value = ts.ToString("dd.MM.yyyy HH:mm");
            plant.Cell(rowNumber, 2).Value = perRowMwh;
            plant.Cell(rowNumber, 3).Value = perRowMwh;
            plant.Cell(rowNumber, 4).Value = perRowBud;
            plant.Cell(rowNumber, 5).Value = 500.0;
            plant.Cell(rowNumber, 6).Value = 0.0;            // Ubalanse — krevd kolonne
            plant.Cell(rowNumber, 7).Value = perRowOppgjor;
            rowNumber++;
        }

        var ms = new MemoryStream();
        workbook.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }
}
