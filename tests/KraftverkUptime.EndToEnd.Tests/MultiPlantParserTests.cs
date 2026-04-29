using FluentAssertions;
using KraftverkUptime.Modules.Settlement.Parsing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// End-to-end-tester for multi-anleggs-parsing mot ekte Dalane Kraft-eksport
/// (`dataeksport_20260429103503.xlsx`). Verifiserer:
///   – 9 anleggs-faner blir til 9 ParsedSettlement-resultater
///   – Kanoniske navn (med æ/ø/å) hentes korrekt fra R1, ikke fra ASCII-stripet fane-navn
///   – PlantId blir slugifisert form av R1-navnet
///   – 15-min granularitet aggregeres til hourly (672 timer for feb-2026)
///   – Eldre enkelt-anlegg-format (Drivdal feb-2025) fortsatt parses gjennom ParseAsync
/// </summary>
public class MultiPlantParserTests
{
    private const string MultiPlantFixturePath = "fixtures/dataeksport_20260429103503.xlsx";
    private const string SinglePlantFixturePath = "fixtures/drivdal-feb2025.xlsx";

    private static ExcelSettlementParser CreateParser() =>
        new(new SettlementSchemaRegistry(), NullLogger<ExcelSettlementParser>.Instance);

    [Fact]
    public async Task ParseAll_DataeksportFormat_Returnerer_9_Resultater()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(MultiPlantFixturePath);
        var all = await parser.ParseAllAsync(stream, default);

        all.Should().HaveCount(9);
    }

    [Fact]
    public async Task ParseAll_BevarerNorske_Tegn_Fra_R1()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(MultiPlantFixturePath);
        var all = await parser.ParseAllAsync(stream, default);

        var navn = all.Select(p => p.PlantName).ToList();
        navn.Should().Contain("Løgjen");
        navn.Should().Contain("Grødemfoss");
        navn.Should().Contain("Øgreyfoss");
        navn.Should().Contain("Ørsdalen");
    }

    [Fact]
    public async Task ParseAll_PlantId_ErSlugifisertNavn()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(MultiPlantFixturePath);
        var all = await parser.ParseAllAsync(stream, default);

        var slugs = all.Select(p => p.PlantId).ToList();
        slugs.Should().BeEquivalentTo(new[]
        {
            "logjen", "drivdal", "grodemfoss", "haukland", "honnefoss",
            "lindland", "ogreyfoss", "orsdalen", "liavatn",
        });
    }

    [Fact]
    public async Task ParseAll_15MinGranularitet_AggregeresTil672Timer()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(MultiPlantFixturePath);
        var all = await parser.ParseAllAsync(stream, default);

        // Februar 2026 = 28 × 24 = 672 timer (ikke skuddår, ingen DST i feb)
        foreach (var plant in all)
        {
            plant.Hourly.Should().HaveCount(672, $"plant {plant.PlantId} skal ha 672 timer etter aggregering");
        }
    }

    [Fact]
    public async Task ParseAll_Periode_Er_Feb2026()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(MultiPlantFixturePath);
        var all = await parser.ParseAllAsync(stream, default);

        // Feb 1, 00:00 Europe/Oslo (CET, +01:00 i februar) = Jan 31, 23:00 UTC
        var expectedStart = new DateTimeOffset(2026, 1, 31, 23, 0, 0, TimeSpan.Zero);
        var expectedLastHour = new DateTimeOffset(2026, 2, 28, 22, 0, 0, TimeSpan.Zero); // 23:00 lokal 28. feb

        foreach (var plant in all)
        {
            plant.PeriodStartUtc.Should().Be(expectedStart);
            plant.PeriodEndUtc.Should().Be(expectedLastHour);
        }
    }

    [Fact]
    public async Task ParseAll_DrivdalSlug_Eksisterer()
    {
        // Drivdal er allerede et anlegg i DB — slug-en MÅ matche.
        var parser = CreateParser();
        await using var stream = File.OpenRead(MultiPlantFixturePath);
        var all = await parser.ParseAllAsync(stream, default);

        var drivdal = all.FirstOrDefault(p => p.PlantId == "drivdal");
        drivdal.Should().NotBeNull();
        drivdal!.PlantName.Should().Be("Drivdal");
    }

    [Fact]
    public async Task ParseAsync_GammelEnkeltAnleggsFormat_FungererFortsatt()
    {
        // Bakoverkompabilitet: Drivdal feb-2025 er gammel single-plant-fil.
        var parser = CreateParser();
        await using var stream = File.OpenRead(SinglePlantFixturePath);
        var parsed = await parser.ParseAsync(stream, default);

        parsed.PlantName.Should().Be("Drivdal");
        parsed.PlantId.Should().BeNull("enkelt-plant-parser lar caller resolve plantId fra URL");
        parsed.Hourly.Should().HaveCount(672);
    }

    [Fact]
    public async Task ParseAll_GammelEnkeltAnleggsFormat_GirEnElement()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(SinglePlantFixturePath);
        var all = await parser.ParseAllAsync(stream, default);

        all.Should().HaveCount(1);
        all[0].PlantName.Should().Be("Drivdal");
        all[0].PlantId.Should().BeNull();
    }

    [Fact]
    public async Task ParseAll_TotalProduksjon_Lgjen_MatcherSummering()
    {
        // R6 i Summering: 'Løgjen', MwhElhub = 52.712768
        var parser = CreateParser();
        await using var stream = File.OpenRead(MultiPlantFixturePath);
        var all = await parser.ParseAllAsync(stream, default);

        var logjen = all.First(p => p.PlantId == "logjen");
        var sum = logjen.Hourly.Sum(r => r.MwhElhub ?? 0);
        sum.Should().BeApproximately(52.712768, 0.01,
            "summen av timer skal matche Summering-fanens aggregat");
    }
}
