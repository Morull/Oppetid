using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Settlement.Parsing;
using KraftverkUptime.Modules.Settlement.Quality;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Parser-nivå enhetstester som verifiserer de spesifikke fallgruvene
/// i Prompt 2: enhetsrad hoppes over, kolonne 17 ignoreres, Elhub == eSett
/// sjekkes med toleranse, alle 672 timer leses.
/// </summary>
public class SettlementParserTests
{
    private const string FixturePath = "fixtures/drivdal-feb2025.xlsx";

    private static ExcelSettlementParser CreateParser() =>
        new(new SettlementSchemaRegistry(), NullLogger<ExcelSettlementParser>.Instance);

    [Fact]
    public async Task Parse_Drivdal_Produces672Hours()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(FixturePath);
        var parsed = await parser.ParseAsync(stream, CancellationToken.None);

        parsed.PlantName.Should().Be("Drivdal");
        parsed.Hourly.Should().HaveCount(672);
        parsed.SchemaVersion.Should().Be("portal-v1");
    }

    [Fact]
    public async Task Parse_Drivdal_SkipsUnitRow_FirstDataHasNumericValue()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(FixturePath);
        var parsed = await parser.ParseAsync(stream, CancellationToken.None);

        var first = parsed.Hourly[0];
        first.MwhElhub.Should().NotBeNull(
            "første datarad skal være faktisk tall – enhetsraden (MWh-tekst) skal hoppes over");
    }

    [Fact]
    public async Task Parse_Drivdal_AllHoursHaveElhubEqualESett_WithinTolerance()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(FixturePath);
        var parsed = await parser.ParseAsync(stream, CancellationToken.None);

        var elhubEsettMismatchIssue = parsed.Issues
            .FirstOrDefault(i => i.Code == "ELHUB_ESETT_MISMATCH");

        // Drivdal feb 2025 har ingen avvik – alle 672 timer har Elhub == eSett
        elhubEsettMismatchIssue.Should().BeNull(
            "Drivdal feb 2025 fiksturen har ikke Elhub/eSett-avvik");
    }

    [Fact]
    public async Task Parse_Drivdal_TotalProduction_Matches703_55466Mwh()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(FixturePath);
        var parsed = await parser.ParseAsync(stream, CancellationToken.None);

        var total = parsed.Hourly.Sum(r => r.MwhElhub ?? 0);
        total.Should().BeApproximately(703.55466, 1e-4,
            "total produksjon skal matche Summering-fane eksakt");
    }

    [Fact]
    public async Task Parse_Drivdal_TimeInUtc_FirstHourIsJan31_23_00()
    {
        var parser = CreateParser();
        await using var stream = File.OpenRead(FixturePath);
        var parsed = await parser.ParseAsync(stream, CancellationToken.None);

        var first = parsed.Hourly[0];
        // Feb 1, 00:00 Europe/Oslo (CET, +01:00 i februar) = Jan 31, 23:00 UTC
        first.TimeUtc.Should().Be(new DateTimeOffset(2025, 1, 31, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task DataQualityReport_ForDrivdal_ReportsAll672HoursAccepted()
    {
        var parser = CreateParser();
        var qualityBuilder = new DataQualityReportBuilder();

        await using var stream = File.OpenRead(FixturePath);
        var parsed = await parser.ParseAsync(stream, CancellationToken.None);
        var (quality, _) = qualityBuilder.Build(parsed);

        quality.HoursExpected.Should().Be(672);
        quality.HoursReceived.Should().Be(672);
        quality.HoursAccepted.Should().Be(672);
        quality.HoursFlagged.Should().Be(0);
        quality.HoursRejected.Should().Be(0);
    }
}
