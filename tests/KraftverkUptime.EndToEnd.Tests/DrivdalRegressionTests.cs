using System.Text.Json;
using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Classification.Analyzers;
using KraftverkUptime.Modules.Classification.Classification;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Classification.Kpi;
using KraftverkUptime.Modules.Settlement.Parsing;
using KraftverkUptime.Modules.Settlement.Quality;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.EndToEnd.Tests;

/// <summary>
/// Regresjonstest: kjører hele pipeline (parser → data quality → klassifisering → KPI)
/// mot den faktiske Drivdal-fiksturen og verifiserer at hver KPI i
/// <c>drivdal-feb2025-fasit.json</c> matcher .NET-implementasjonens output
/// innenfor numerisk toleranse.
///
/// Fasiten ble generert av Python-PoC-en og er kontrakten .NET-implementasjonen
/// må holde.
/// </summary>
public class DrivdalRegressionTests
{
    private const string FixturePath = "fixtures/drivdal-feb2025.xlsx";
    private const string FasitPath = "fixtures/drivdal-feb2025-fasit.json";

    private const double RatioTolerance = 1e-9;
    private const double MwhTolerance = 1e-4;
    private const double NokTolerance = 1e-2;
    private const double HoursTolerance = 1e-6;

    [Fact]
    public async Task FullPipeline_MatchesFasit_ForDrivdalFebruar2025()
    {
        // --- Arrange: bygg pipeline ---
        var schema = new SettlementSchemaRegistry();
        var parser = new ExcelSettlementParser(schema, NullLogger<ExcelSettlementParser>.Instance);
        var qualityBuilder = new DataQualityReportBuilder();
        var classifier = new SettlementClassifier();
        var kpiCalc = new UptimeKpiCalculator();
        var analyzer = new SettlementUptimeAnalyzer(
            classifier, kpiCalc, qualityBuilder, NullLogger<SettlementUptimeAnalyzer>.Instance);

        var plantConfig = new PlantClassificationConfig
        {
            PlantId = "Drivdal",
            PlantType = PlantType.Regulated,
            NominalPowerMw = 2.2,
            DeratingThreshold = 0.90,
            SustainedStopHours = 24,
            MarginalCostNokMwh = 100.0,
        };

        // --- Act ---
        await using var stream = File.OpenRead(FixturePath);
        var parsed = await parser.ParseAsync(stream, CancellationToken.None);

        var input = new UptimePeriod(parsed, plantConfig);
        var report = await analyzer.AnalyzeAsync(input, CancellationToken.None);

        // --- Assert: load fasit ---
        var fasitJson = await File.ReadAllTextAsync(FasitPath);
        using var fasit = JsonDocument.Parse(fasitJson);
        var fasitKpis = fasit.RootElement.GetProperty("kpis");
        var fasitStates = fasit.RootElement.GetProperty("state_counts");

        // Periode
        report.PeriodHours.Should().Be(fasit.RootElement.GetProperty("period_hours").GetInt32());
        report.PlantId.Should().Be(fasit.RootElement.GetProperty("plant_id").GetString());

        // State-fordeling
        VerifyStateCount(report, fasitStates, "InService", UnitState.InService);
        VerifyStateCount(report, fasitStates, "ForcedOutage", UnitState.ForcedOutage);
        VerifyStateCount(report, fasitStates, "PlannedOutage", UnitState.PlannedOutage);
        VerifyStateCount(report, fasitStates, "ReserveShutdown", UnitState.ReserveShutdown);
        VerifyStateCount(report, fasitStates, "ForcedDerating", UnitState.ForcedDerating);

        // Hver KPI i fasiten skal finnes og matche
        foreach (var kpiName in fasitKpis.EnumerateObject().Select(p => p.Name))
        {
            var expected = fasitKpis.GetProperty(kpiName);
            if (!expected.TryGetProperty("value", out var expectedValueElem))
            {
                continue;
            }
            var unit = expected.GetProperty("unit").GetString();
            var actual = report.Kpis.FirstOrDefault(k => k.Name == kpiName);

            actual.Should().NotBeNull($"KPI '{kpiName}' mangler i .NET-output");

            if (expectedValueElem.ValueKind == JsonValueKind.Null)
            {
                actual!.Value.Should().BeNull($"KPI '{kpiName}' forventet null");
                continue;
            }

            var expectedValue = expectedValueElem.GetDouble();
            actual!.Value.Should().NotBeNull($"KPI '{kpiName}' forventet verdi men fikk null");
            var tolerance = ToleranceFor(unit);
            actual.Value!.Value.Should().BeApproximately(expectedValue, tolerance,
                $"KPI '{kpiName}' ({unit}) skal matche fasit");
        }
    }

    private static void VerifyStateCount(
        UptimeReport report,
        JsonElement fasitStates,
        string stateName,
        UnitState state)
    {
        if (!fasitStates.TryGetProperty(stateName, out var element))
        {
            return;
        }
        var expected = element.GetInt32();
        var actual = report.StateCounts.GetValueOrDefault(state);
        actual.Should().Be(expected, $"State-count for {stateName} skal matche fasit");
    }

    private static double ToleranceFor(string? unit) => unit switch
    {
        "ratio" => RatioTolerance,
        "correlation" => 1e-6,   // korrelasjon er numerisk mer følsom
        "MWh" => MwhTolerance,
        "NOK" => NokTolerance,
        "hours" => HoursTolerance,
        "events" => 0,           // heltall – eksakt match
        _ => 1e-6,
    };
}
