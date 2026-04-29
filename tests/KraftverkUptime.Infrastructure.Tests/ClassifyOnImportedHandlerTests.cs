using FluentAssertions;
using KraftverkUptime.Core.Analysis;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Events;
using KraftverkUptime.Modules.Classification.Config;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Dtos;
using KraftverkUptime.Modules.Settlement.Jobs;
using KraftverkUptime.Modules.Settlement.Quality;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

public class ClassifyOnImportedHandlerTests
{
    [Fact]
    public async Task Handle_Calls_Provider_And_Analyzer_And_Persists_Report()
    {
        var from = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var expectedPeriod = new UptimePeriod(
            new ParsedSettlement
            {
                PlantName = "Testanlegg",
                SchemaVersion = "v1",
                PeriodStartUtc = from,
                PeriodEndUtc = to,
                Hourly = Array.Empty<SettlementHourlyRow>(),
                Issues = Array.Empty<ValidationIssue>(),
            },
            new PlantClassificationConfig
            {
                PlantId = "plant-1",
                PlantType = PlantType.Regulated,
                NominalPowerMw = 10.0,
            });

        var expectedReport = new UptimeReport
        {
            PlantId = "plant-1",
            PeriodStartUtc = from,
            PeriodEndUtc = to,
            PeriodHours = 0,
            StateCounts = new Dictionary<UnitState, int>(),
            Classified = Array.Empty<ClassifiedHourlyRow>(),
            Kpis = Array.Empty<KpiResult>(),
        };

        var provider = new FakePeriodProvider(expectedPeriod);
        var analyzer = new FakeAnalyzer(expectedReport);
        var store = new FakeReportStore();

        var handler = new ClassifyOnImportedHandler(
            provider, analyzer, store, NullLogger<ClassifyOnImportedHandler>.Instance);

        await handler.HandleAsync(new SettlementImportedEvent
        {
            PlantId = "plant-1",
            OwnerOrgId = "org-1",
            BlobPath = "plants/plant-1/2026-02.xlsx",
            IdempotencyKey = "abc123",
            PeriodStartUtc = from,
            PeriodEndUtc = to,
            HourCount = 0,
            IssueCount = 0,
        }, CancellationToken.None);

        provider.LastAssetId.Should().Be("plant-1");
        provider.LastFromUtc.Should().Be(from);
        provider.LastToUtc.Should().Be(to);
        analyzer.LastInput.Should().BeSameAs(expectedPeriod);

        store.SaveCount.Should().Be(1);
        store.LastOwnerOrgId.Should().Be("org-1");
        store.LastPlantId.Should().Be("plant-1");
        store.LastIdempotencyKey.Should().Be("abc123");
        store.LastReport.Should().BeSameAs(expectedReport);
    }

    private sealed class FakePeriodProvider : IUptimePeriodProvider
    {
        private readonly UptimePeriod _period;
        public string? LastAssetId;
        public DateTimeOffset LastFromUtc;
        public DateTimeOffset LastToUtc;

        public FakePeriodProvider(UptimePeriod period) => _period = period;

        public Task<UptimePeriod> GetAsync(
            string assetId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            LastAssetId = assetId;
            LastFromUtc = fromUtc;
            LastToUtc = toUtc;
            return Task.FromResult(_period);
        }
    }

    private sealed class FakeAnalyzer : IAnalyzer<UptimePeriod, UptimeReport>
    {
        private readonly UptimeReport _report;
        public UptimePeriod? LastInput;

        public FakeAnalyzer(UptimeReport report) => _report = report;

        public Task<UptimeReport> AnalyzeAsync(UptimePeriod input, CancellationToken ct)
        {
            LastInput = input;
            return Task.FromResult(_report);
        }
    }

    private sealed class FakeReportStore : IUptimeReportStore
    {
        public int SaveCount { get; private set; }
        public string? LastOwnerOrgId { get; private set; }
        public string? LastPlantId { get; private set; }
        public string? LastIdempotencyKey { get; private set; }
        public UptimeReport? LastReport { get; private set; }

        public Task SaveAsync(
            string ownerOrgId, string plantId, string idempotencyKey,
            UptimeReport report, CancellationToken ct)
        {
            SaveCount++;
            LastOwnerOrgId = ownerOrgId;
            LastPlantId = plantId;
            LastIdempotencyKey = idempotencyKey;
            LastReport = report;
            return Task.CompletedTask;
        }

        public Task<UptimeReport?> GetAsync(
            string ownerOrgId, string plantId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(LastReport);

        public Task DeleteAsync(string ownerOrgId, string plantId, string idempotencyKey, CancellationToken ct)
            => Task.CompletedTask;
        public Task<int> DeleteAllForPlantAsync(string ownerOrgId, string plantId, CancellationToken ct)
            => Task.FromResult(0);
        public Task<int> DeleteAllAsync(string ownerOrgId, CancellationToken ct)
            => Task.FromResult(0);
    }
}
