using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Reporting;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Dtos;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Reporting;

/// <summary>
/// Regresjon for FAGVURDERING-KPI-BEREGNINGER #4: dag-CR-serien (scatter/
/// histogram) MÅ bruke samme grunnlag som dag-CR-KPI-en —
/// Σ(MWh × spot) / Σ(MWh), IKKE NokDay/MwhDay (faktisk omsetning). Tidligere
/// brukte <see cref="CaptureRateQueryService.GetDailySeriesAsync"/> det gamle
/// grunnlaget, så grafen motsa KPI-kortet (~9 pp i kjent Øgreyfoss-case).
/// </summary>
public class CaptureRateQueryServiceTests
{
    [Fact]
    public async Task GetDailySeries_BrukerElhubSpotValue_IkkeFaktiskOmsetning()
    {
        // Ett døgn, 24 like timer: MWh=10, spot=100 → Σ(MWh×spot)/MWh = 100.
        // SpotomsetningNok settes bevisst til HALVPARTEN (500/t, ikke 1000) for
        // å skille de to grunnlagene: gammelt (buggy) NokDay/MwhDay = 50, nytt
        // ElhubSpotValueDay/MwhDay = 100.
        var dayStartUtc = new DateTimeOffset(2026, 3, 15, 0, 0, 0, TimeSpan.Zero);
        var rows = new List<ClassifiedHourlyRow>();
        for (var h = 0; h < 24; h++)
        {
            rows.Add(new ClassifiedHourlyRow
            {
                Row = new SettlementHourlyRow
                {
                    TimeUtc = dayStartUtc.AddHours(h),
                    TimeLocal = dayStartUtc.AddHours(h),
                    MwhElhub = 10,
                    SpotprisNokMwh = 100,
                    SpotomsetningNok = 500, // ≠ 10 × 100 = 1000 (bevisst)
                },
                State = UnitState.InService,
                CauseCode = "test",
                Confidence = 1.0,
                Rationale = "test",
            });
        }

        var report = new UptimeReport
        {
            PlantId = "drivdal",
            PeriodStartUtc = dayStartUtc,
            PeriodEndUtc = dayStartUtc.AddDays(1),
            PeriodHours = 24,
            StateCounts = new Dictionary<UnitState, int> { [UnitState.InService] = 24 },
            Classified = rows,
            Kpis = new List<KpiResult>(),
        };

        var svc = new CaptureRateQueryService(
            new StubStore(report), new StubStore(report),
            NullLogger<CaptureRateQueryService>.Instance);

        var daily = await svc.GetDailySeriesAsync(
            "drivdal", dayStartUtc, dayStartUtc.AddDays(1), CancellationToken.None);

        // 24 UTC-timer fra midnatt fordeler seg på to lokale Oslo-døgn — antallet
        // er ikke poenget; poenget er at HVERT døgn bruker ElhubSpotValue-grunnlaget.
        daily.Should().NotBeEmpty();
        daily.Should().AllSatisfy(d =>
        {
            // Volumvektet oppnådd spotpris = 100 (ikke 50 fra faktisk omsetning).
            d.OppnaaddNokMwh.Should().BeApproximately(100, 1e-6);
            // RaCr = oppnådd / dag-snitt-spot = 100/100 = 1,0 (ikke 0,5).
            d.RaCr.Should().BeApproximately(1.0, 1e-6);
        });
    }

    // Kombinert stub for ISettlementImportRecorder + IUptimeReportStore — gir
    // alltid samme rapport/import (nok for dag-serie-testen). Ubrukte medlemmer
    // kaster/returnerer defaults.
    private sealed class StubStore : ISettlementImportRecorder, IUptimeReportStore
    {
        private readonly UptimeReport _report;

        public StubStore(UptimeReport report) => _report = report;

        public Task<IReadOnlyList<SettlementImportRecord>> ListForPlantAsync(
            string plantId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int limit, CancellationToken ct)
        {
            var record = new SettlementImportRecord
            {
                OwnerOrgId = "test",
                PlantId = plantId,
                IdempotencyKey = "k1",
                BlobPath = "test://",
                PlantName = plantId,
                SchemaVersion = "test",
                PeriodStartUtc = _report.PeriodStartUtc,
                PeriodEndUtc = _report.PeriodEndUtc,
                HourCount = _report.PeriodHours,
                IssueCount = 0,
                ImportedAtUtc = _report.PeriodStartUtc,
            };
            return Task.FromResult<IReadOnlyList<SettlementImportRecord>>(new[] { record });
        }

        public Task<UptimeReport?> GetAsync(string ownerOrgId, string plantId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult<UptimeReport?>(_report);

        // ---- ubrukte medlemmer ----
        public Task RecordAsync(SettlementImportRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task<SettlementImportRecord?> FindLatestCoveringAsync(string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult<SettlementImportRecord?>(null);
        public Task<SettlementImportRecord?> FindByIdempotencyKeyAsync(string plantId, string idempotencyKey, CancellationToken ct) =>
            Task.FromResult<SettlementImportRecord?>(null);
        public Task<bool> DeleteAsync(string plantId, string idempotencyKey, CancellationToken ct) => Task.FromResult(false);
        public Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct) => Task.FromResult(0);
        public Task<int> DeleteAllAsync(string ownerOrgId, CancellationToken ct) => Task.FromResult(0);

        public Task SaveAsync(string ownerOrgId, string plantId, string idempotencyKey, UptimeReport report, CancellationToken ct) =>
            Task.CompletedTask;
        public Task DeleteAsync(string ownerOrgId, string plantId, string idempotencyKey, CancellationToken ct) => Task.CompletedTask;
        public Task<int> DeleteAllForPlantAsync(string ownerOrgId, string plantId, CancellationToken ct) => Task.FromResult(0);
        Task<int> IUptimeReportStore.DeleteAllAsync(string ownerOrgId, CancellationToken ct) => Task.FromResult(0);
    }
}
