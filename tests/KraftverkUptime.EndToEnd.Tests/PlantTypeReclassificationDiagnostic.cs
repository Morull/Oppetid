using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Classification;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Settlement.Parsing;
using KraftverkUptime.Modules.Settlement.Quality;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Diagnose-test for SPEC-MVP-HARDENING tiltak D: viser hvor mange timer som
/// re-klassifiseres for Lindland og Ørsdalen når PlantType endres fra
/// Regulated → RunOfRiver. Kjører ren klassifikator-pipeline mot
/// <c>dataeksport_20260429103503.xlsx</c> (feb-2026) og dumper state-counts
/// for begge varianter side om side.
///
/// Testen feiler aldri — formålet er å gi drifts-leder en konkret rapport
/// før commit. Resultatet leses fra test-output (xunit ITestOutputHelper).
/// </summary>
public class PlantTypeReclassificationDiagnostic
{
    private const string MultiPlantFixturePath = "fixtures/dataeksport_20260429103503.xlsx";

    private readonly ITestOutputHelper _output;

    public PlantTypeReclassificationDiagnostic(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Diagnostikk: PlantType-endring for elvekraft-anlegg (feb-2026)")]
    [InlineData("lindland", "Lindland (kaskade m/24t-lag → fungerer som elvekraft)")]
    [InlineData("orsdalen", "Ørsdalen (ren elvekraft)")]
    public async Task Diff_RegulatedVsRunOfRiver_ForCandidatePlant(string plantId, string description)
    {
        // ----- Arrange -----
        var parser = new ExcelSettlementParser(
            new SettlementSchemaRegistry(),
            NullLogger<ExcelSettlementParser>.Instance);

        await using var stream = File.OpenRead(MultiPlantFixturePath);
        var allPlants = await parser.ParseAllAsync(stream, CancellationToken.None);
        var plant = allPlants.FirstOrDefault(p => p.PlantId == plantId);

        if (plant is null)
        {
            _output.WriteLine($"⚠ {plantId} finnes ikke i fixturen — hopper over.");
            return;
        }

        var qualityBuilder = new DataQualityReportBuilder();
        var (_, enriched) = qualityBuilder.Build(plant);
        var classifier = new SettlementClassifier();

        // ----- Act: klassifiser med begge PlantType-er -----
        var asRegulated = classifier.Classify(enriched, ConfigFor(plantId, PlantType.Regulated));
        var asRunOfRiver = classifier.Classify(enriched, ConfigFor(plantId, PlantType.RunOfRiver));

        // ----- Build diff -----
        var changes = 0;
        var changeMatrix = new Dictionary<(UnitState From, UnitState To), int>();
        for (var i = 0; i < asRegulated.Count; i++)
        {
            var before = asRegulated[i].State;
            var after = asRunOfRiver[i].State;
            if (before != after)
            {
                changes++;
                changeMatrix[(before, after)] = changeMatrix.GetValueOrDefault((before, after)) + 1;
            }
        }

        // ----- Report -----
        _output.WriteLine($"================================================================");
        _output.WriteLine($"  {description}");
        _output.WriteLine($"  Periode: {enriched.First().TimeUtc.UtcDateTime:yyyy-MM-dd} → {enriched.Last().TimeUtc.UtcDateTime:yyyy-MM-dd}");
        _output.WriteLine($"  Antall timer: {enriched.Count}");
        _output.WriteLine($"================================================================");

        DumpStateCounts("FØR (Regulated)", asRegulated);
        DumpStateCounts("ETTER (RunOfRiver)", asRunOfRiver);

        _output.WriteLine($"");
        _output.WriteLine($"--- Endring ---");
        _output.WriteLine($"Timer re-klassifisert: {changes} av {enriched.Count} ({100.0 * changes / enriched.Count:F1} %)");
        if (changeMatrix.Count > 0)
        {
            _output.WriteLine($"Diff-matrise:");
            foreach (var ((from, to), count) in changeMatrix.OrderByDescending(kv => kv.Value))
            {
                _output.WriteLine($"  {from} → {to}: {count} t");
            }
        }
        else
        {
            _output.WriteLine($"  (ingen endring — anlegget hadde ingen 0/0-timer)");
        }
        _output.WriteLine($"");

        // Sanity: bare 0/0-timer skal endre seg, og bare ReserveShutdown→ResourceUnavailable.
        foreach (var ((from, to), _) in changeMatrix)
        {
            from.Should().Be(UnitState.ReserveShutdown,
                "kun ReserveShutdown skal kunne endres av PlantType-flagget");
            to.Should().Be(UnitState.ResourceUnavailable,
                "RunOfRiver flytter ReserveShutdown → ResourceUnavailable");
        }
    }

    private void DumpStateCounts(string label, IReadOnlyList<ClassifiedHourlyRow> rows)
    {
        _output.WriteLine($"");
        _output.WriteLine($"--- {label} ---");
        var counts = rows.GroupBy(r => r.State)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var state in Enum.GetValues<UnitState>())
        {
            var n = counts.GetValueOrDefault(state);
            if (n > 0)
            {
                _output.WriteLine($"  {state,-25} {n,5} t");
            }
        }
    }

    private static PlantClassificationConfig ConfigFor(string plantId, PlantType type) => new()
    {
        PlantId = plantId,
        PlantType = type,
        NominalPowerMw = 4.0,
        DeratingThreshold = 0.80,
    };
}
