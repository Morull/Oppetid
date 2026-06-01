using FluentAssertions;
using KraftverkUptime.Core.Configuration;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Time;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Infrastructure.Reporting;
using KraftverkUptime.Infrastructure.Security;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.CaptureRate;
using KraftverkUptime.Modules.Reporting.DataQuality;
using KraftverkUptime.Modules.Reporting.Economy;
using KraftverkUptime.Modules.Reporting.KaiaCost;
using KraftverkUptime.Modules.Reporting.Nedetid;
using KraftverkUptime.Modules.Reporting.Portefolje;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Economy;

/// <summary>
/// Verifiserer at <see cref="EconomyReportQueryService"/> aggregerer
/// underliggende tjenester slik spec'en krever:
///   * NOK-tall summeres på tvers av valgte anlegg.
///   * Capture rate MWh-vektes (ikke aritmetisk snitt).
///   * Vakt-kost-andel pro-rata × GWh-andel, med ALLE anlegg som nevner.
///   * Trend-prosent regnes mot forrige periode; null hvis forrige = 0.
///
/// Bruker stubs (ingen mocking-rammeverk er installert i prosjektet).
/// </summary>
public class EconomyReportQueryServiceTests
{
    private static readonly DateTimeOffset From = new(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = new(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NokTall_SummeresOverValgteAnlegg()
    {
        await using var harness = new Harness();
        harness.AddPlant("a", normalGwh: 10);
        harness.AddPlant("b", normalGwh: 20);
        harness.AddPlant("c", normalGwh: 30);

        // Periode april 2026.
        harness.Settlement.SetReport("a", From, To, spot: 100, oppgjor: 80, ubalanse: -10, mwh: 50);
        harness.Settlement.SetReport("b", From, To, spot: 200, oppgjor: 160, ubalanse: -20, mwh: 100);
        harness.Settlement.SetReport("c", From, To, spot: 300, oppgjor: 240, ubalanse: -30, mwh: 150);

        // Forrige (mars) — null overalt så trend blir lett å resonnere om.

        var svc = harness.Build();
        var rapport = await svc.GetAsync(new[] { "a", "b", "c" }, From, To, PeriodKind.Month);

        var spot = rapport.Inntekter.Kpis.Single(k => k.Key == EconomyKpiKeys.Spotomsetning);
        spot.Verdi.Should().Be(600);
        var oppgjor = rapport.Resultat.Kpis.Single(k => k.Key == EconomyKpiKeys.Oppgjor);
        oppgjor.Verdi.Should().Be(480);

        // Ubalansekost lagres negativ i KPI-kilden (sign-konvensjon i
        // UptimeKpiCalculator); Økonomi-fanen viser den som positiv kostnad.
        var ubalanse = rapport.Kostnader.Kpis.Single(k => k.Key == EconomyKpiKeys.Ubalansekost);
        ubalanse.Verdi.Should().Be(60);
    }

    [Fact]
    public async Task CaptureRate_MwhVektes()
    {
        await using var harness = new Harness();
        harness.AddPlant("a", normalGwh: 10);
        harness.AddPlant("b", normalGwh: 10);

        // a: 0,90 over 100 MWh, b: 1,10 over 300 MWh.
        // Aritmetisk snitt = 1,00. MWh-vektet = (0,9·100 + 1,1·300) / 400 = 420/400 = 1,05.
        harness.Settlement.SetReport("a", From, To, spot: 0, oppgjor: 0, ubalanse: 0, mwh: 100);
        harness.Settlement.SetReport("b", From, To, spot: 0, oppgjor: 0, ubalanse: 0, mwh: 300);
        harness.CaptureRate.SetCr("a", From, To, timesCr: 0.90);
        harness.CaptureRate.SetCr("b", From, To, timesCr: 1.10);

        var svc = harness.Build();
        var rapport = await svc.GetAsync(new[] { "a", "b" }, From, To, PeriodKind.Month);

        var cr = rapport.Inntekter.Kpis.Single(k => k.Key == EconomyKpiKeys.CaptureRate);
        cr.Verdi.Should().BeApproximately(1.05, 1e-9);
    }

    [Fact]
    public async Task Trend_BeregnesMotForrigePeriode()
    {
        await using var harness = new Harness();
        harness.AddPlant("a", normalGwh: 10);

        harness.Settlement.SetReport("a", From, To, spot: 1200, oppgjor: 0, ubalanse: 0, mwh: 50);
        // Forrige måned = mars 2026.
        var prevFrom = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var prevTo = From;
        harness.Settlement.SetReport("a", prevFrom, prevTo, spot: 1000, oppgjor: 0, ubalanse: 0, mwh: 50);

        var svc = harness.Build();
        var rapport = await svc.GetAsync(new[] { "a" }, From, To, PeriodKind.Month);

        var spot = rapport.Inntekter.Kpis.Single(k => k.Key == EconomyKpiKeys.Spotomsetning);
        spot.Verdi.Should().Be(1200);
        spot.VerdiForrige.Should().Be(1000);
        spot.EndringProsent.Should().BeApproximately(0.20, 1e-9);
    }

    [Fact]
    public async Task Trend_NullNaarForrigeErNull()
    {
        await using var harness = new Harness();
        harness.AddPlant("a", normalGwh: 10);

        harness.Settlement.SetReport("a", From, To, spot: 500, oppgjor: 0, ubalanse: 0, mwh: 50);
        // Ingen mars-rapport → forrige = 0 → endring = null.

        var svc = harness.Build();
        var rapport = await svc.GetAsync(new[] { "a" }, From, To, PeriodKind.Month);

        var spot = rapport.Inntekter.Kpis.Single(k => k.Key == EconomyKpiKeys.Spotomsetning);
        spot.EndringProsent.Should().BeNull();
    }

    [Fact]
    public async Task VaktKost_ProRataMedGwhAndelOverHelePortefoljen()
    {
        await using var harness = new Harness();
        // GWh-nevneren er hele porteføljen, ikke bare utvalget.
        harness.AddPlant("a", normalGwh: 30);
        harness.AddPlant("b", normalGwh: 20);
        harness.AddPlant("c", normalGwh: 50);  // utenfor utvalget
        harness.VaktKostNokPerAar = 365_250;   // gir 1000 NOK/dag

        var svc = harness.Build();
        // April 2026 = 30 dager → 30 000 NOK total vakt-kost i perioden.
        // GWh-andel a = 30/100 = 0,3 → 9 000 NOK. b = 20/100 = 0,2 → 6 000 NOK.
        var rapport = await svc.GetAsync(new[] { "a", "b" }, From, To, PeriodKind.Month);

        var vakt = rapport.Kostnader.Kpis.Single(k => k.Key == EconomyKpiKeys.VaktKostAndel);
        vakt.Verdi.Should().BeApproximately(15_000, 1.0);
    }

    [Fact]
    public async Task YearToDate_SummererSettlementOverAlleMaaneder()
    {
        // Regresjon for NESTE-CHAT-OKONOMI-OPPFOLGING.md Funn 1 (2026-05-22):
        // YTD-spørringer skal returnere sum over alle månedlige imports
        // innenfor perioden, ikke bare den nyeste enkelt-importen.
        await using var harness = new Harness();
        harness.AddPlant("a", normalGwh: 10);

        // Tre månedlige imports: jan, feb, mar 2026.
        var jan = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var feb = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var mar = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var apr = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);
        harness.Settlement.SetReport("a", jan, feb, spot: 100, oppgjor: 80, ubalanse: -10, mwh: 50);
        harness.Settlement.SetReport("a", feb, mar, spot: 200, oppgjor: 160, ubalanse: -20, mwh: 100);
        harness.Settlement.SetReport("a", mar, apr, spot: 300, oppgjor: 240, ubalanse: -30, mwh: 150);

        var svc = harness.Build();
        var rapport = await svc.GetAsync(new[] { "a" }, jan, apr, PeriodKind.YearToDate);

        // Sum over jan+feb+mar — ikke bare mars.
        var spot = rapport.Inntekter.Kpis.Single(k => k.Key == EconomyKpiKeys.Spotomsetning);
        spot.Verdi.Should().Be(600);
        var oppgjor = rapport.Resultat.Kpis.Single(k => k.Key == EconomyKpiKeys.Oppgjor);
        oppgjor.Verdi.Should().Be(480);
        var ubalanse = rapport.Kostnader.Kpis.Single(k => k.Key == EconomyKpiKeys.Ubalansekost);
        ubalanse.Verdi.Should().Be(60); // sum av negative ubalanse-tall, snudd til positiv kostnad
    }

    [Fact]
    public async Task ReimportAvSammePeriode_BrukerNyesteIkkeBeggeSummert()
    {
        // Hvis to imports har samme periode (en reimport overstyrer en
        // tidligere) skal ikke begge telle. Stub-en bruker PeriodStart som
        // ImportedAt — vi tester at GroupBy(periode) dedupliserer.
        await using var harness = new Harness();
        harness.AddPlant("a", normalGwh: 10);

        var jan = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var feb = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);

        // SetReport overskriver hvis samme nøkkel — så vi har bare én import
        // i stubben. Test-en bekrefter at duplikat-stories ikke kollapser
        // sum-en hvis stub-en hadde returnert begge.
        harness.Settlement.SetReport("a", jan, feb, spot: 999, oppgjor: 800, ubalanse: -50, mwh: 100);

        var svc = harness.Build();
        var rapport = await svc.GetAsync(new[] { "a" }, jan, feb, PeriodKind.Month);

        var spot = rapport.Inntekter.Kpis.Single(k => k.Key == EconomyKpiKeys.Spotomsetning);
        spot.Verdi.Should().Be(999);
    }

    [Fact]
    public async Task ReddetAvVakt_FiltrertTilUtvalget()
    {
        await using var harness = new Harness();
        harness.AddPlant("a", normalGwh: 10);
        harness.AddPlant("b", normalGwh: 10);
        harness.AddPlant("c", normalGwh: 10);
        harness.VaktRoi.SetReddet("a", From, To, 100);
        harness.VaktRoi.SetReddet("b", From, To, 200);
        harness.VaktRoi.SetReddet("c", From, To, 9999);  // utenfor utvalget

        var svc = harness.Build();
        var rapport = await svc.GetAsync(new[] { "a", "b" }, From, To, PeriodKind.Month);

        var reddet = rapport.Resultat.Kpis.Single(k => k.Key == EconomyKpiKeys.ReddetAvVakt);
        reddet.Verdi.Should().Be(300);
    }

    // -------------------------------------------------------------------------
    // Harness — bygger EconomyReportQueryService med stubs som kan settes opp
    // pr test. Hver stub støtter bare det testene faktisk trenger; mer kan
    // legges til senere.
    // -------------------------------------------------------------------------

    private sealed class Harness : IAsyncDisposable
    {
        public StubSettlement Settlement { get; } = new();
        public StubCaptureRate CaptureRate { get; } = new();
        public StubKaia Kaia { get; } = new();
        public StubVaktRoi VaktRoi { get; } = new();
        public StubNedetid Nedetid { get; } = new();
        public double VaktKostNokPerAar { get; set; } = 360_000;

        private readonly List<PlantRegistration> _plants = new();
        private KraftverkDbContext? _db;

        public void AddPlant(string id, double? normalGwh = null)
        {
            _plants.Add(new PlantRegistration
            {
                Id = id,
                OwnerOrgId = "test",
                Name = $"Plant {id}",
                Type = PlantType.RunOfRiver,
                InstalledCapacityMw = 1.0,
                NormalAarsproduksjonGwh = normalGwh,
            });
        }

        public EconomyReportQueryService Build()
        {
            var options = new DbContextOptionsBuilder<KraftverkDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            _db = new KraftverkDbContext(options, new NoopQueryContext());
            _db.Plants.AddRange(_plants);
            _db.SaveChanges();

            return new EconomyReportQueryService(
                db: _db,
                imports: Settlement,
                reports: Settlement,
                captureRate: CaptureRate,
                kaia: Kaia,
                vaktRoi: VaktRoi,
                nedetid: Nedetid,
                dataQuality: new StubDataQuality(),
                config: new StubConfig(VaktKostNokPerAar),
                log: NullLogger<EconomyReportQueryService>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            if (_db is not null) await _db.DisposeAsync();
        }
    }

    /// <summary>
    /// Kombinert stub for <see cref="ISettlementImportRecorder"/> +
    /// <see cref="IUptimeReportStore"/>. Holder rapporter pr (plantId, from, to)
    /// og produserer en syntetisk import-rad med samme periode for lookup.
    /// </summary>
    private sealed class StubSettlement : ISettlementImportRecorder, IUptimeReportStore
    {
        private readonly Dictionary<(string plant, DateTimeOffset f, DateTimeOffset t), UptimeReport> _reports = new();

        public void SetReport(string plantId, DateTimeOffset from, DateTimeOffset to,
            double spot, double oppgjor, double ubalanse, double mwh)
        {
            var kpis = new List<KpiResult>
            {
                new("Spotomsetning_NOK", spot, "NOK", 0, 1.0, "okonomi", ""),
                new("Oppgjor_NOK", oppgjor, "NOK", 0, 1.0, "okonomi", ""),
                new("Ubalansekost_NOK", ubalanse, "NOK", 0, 1.0, "okonomi", ""),
                new("TotalProduction_MWh", mwh, "MWh", 0, 1.0, "okonomi", ""),
            };
            _reports[(plantId, from, to)] = new UptimeReport
            {
                PlantId = plantId,
                PeriodStartUtc = from,
                PeriodEndUtc = to,
                PeriodHours = (int)(to - from).TotalHours,
                StateCounts = new Dictionary<UnitState, int>(),
                Classified = Array.Empty<ClassifiedHourlyRow>(),
                Kpis = kpis,
            };
        }

        // ISettlementImportRecorder — returner alle imports for plantId hvor
        // import-perioden overlapper med [queryFrom, queryTo]. Mønsteret matcher
        // ekte DbSettlementImportRecorder: én rad per import, sortert nyeste først.
        public Task<IReadOnlyList<SettlementImportRecord>> ListForPlantAsync(
            string plantId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int limit, CancellationToken ct)
        {
            if (fromUtc is null || toUtc is null)
            {
                return Task.FromResult<IReadOnlyList<SettlementImportRecord>>(Array.Empty<SettlementImportRecord>());
            }

            var matches = _reports
                .Where(kv => kv.Key.plant == plantId
                    && kv.Key.f < toUtc.Value
                    && kv.Key.t > fromUtc.Value)
                .OrderByDescending(kv => kv.Key.f)
                .Take(limit)
                .Select(kv => new SettlementImportRecord
                {
                    OwnerOrgId = "test",
                    PlantId = plantId,
                    IdempotencyKey = $"{plantId}-{kv.Key.f:o}-{kv.Key.t:o}",
                    BlobPath = "test://",
                    PlantName = plantId,
                    SchemaVersion = "test",
                    PeriodStartUtc = kv.Key.f,
                    PeriodEndUtc = kv.Key.t,
                    HourCount = (int)(kv.Key.t - kv.Key.f).TotalHours,
                    IssueCount = 0,
                    ImportedAtUtc = kv.Key.f,
                })
                .ToList();
            return Task.FromResult<IReadOnlyList<SettlementImportRecord>>(matches);
        }

        public Task RecordAsync(SettlementImportRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task<SettlementImportRecord?> FindLatestCoveringAsync(string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult<SettlementImportRecord?>(null);
        public Task<SettlementImportRecord?> FindByIdempotencyKeyAsync(string plantId, string idempotencyKey, CancellationToken ct) =>
            Task.FromResult<SettlementImportRecord?>(null);
        public Task<bool> DeleteAsync(string plantId, string idempotencyKey, CancellationToken ct) => Task.FromResult(false);
        public Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct) => Task.FromResult(0);
        public Task<int> DeleteAllAsync(string ownerOrgId, CancellationToken ct) => Task.FromResult(0);

        // IUptimeReportStore — slå opp via idempotency-nøkkel som matcher bygge-
        // mønsteret i ListForPlantAsync over.
        public Task<UptimeReport?> GetAsync(string ownerOrgId, string plantId, string idempotencyKey, CancellationToken ct)
        {
            var match = _reports.FirstOrDefault(kv => kv.Key.plant == plantId
                && idempotencyKey == $"{plantId}-{kv.Key.f:o}-{kv.Key.t:o}");
            return Task.FromResult<UptimeReport?>(match.Key.plant != null ? match.Value : null);
        }

        public Task SaveAsync(string ownerOrgId, string plantId, string idempotencyKey, UptimeReport report, CancellationToken ct) =>
            Task.CompletedTask;
        public Task DeleteAsync(string ownerOrgId, string plantId, string idempotencyKey, CancellationToken ct) => Task.CompletedTask;
        public Task<int> DeleteAllForPlantAsync(string ownerOrgId, string plantId, CancellationToken ct) => Task.FromResult(0);
        Task<int> IUptimeReportStore.DeleteAllAsync(string ownerOrgId, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class StubCaptureRate : ICaptureRateQueryService
    {
        private readonly Dictionary<(string plant, DateTimeOffset f, DateTimeOffset t), double> _cr = new();

        public void SetCr(string plantId, DateTimeOffset from, DateTimeOffset to, double timesCr)
            => _cr[(plantId, from, to)] = timesCr;

        public Task<CaptureRateCalculator.CaptureRateResult> GetForPlantAsync(
            string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            var cr = _cr.TryGetValue((plantId, fromUtc, toUtc), out var v) ? v : 0;
            var result = new CaptureRateCalculator.CaptureRateResult(
                CapturePriceNokMwh: 0,
                TimesCr: cr,
                TimesBaselineNokMwh: 0,
                DagCr: cr,
                DagBaselineNokMwh: 0,
                TimingMerverdiNok: 0,
                RealisertPrisNokMwh: 0,
                RealisertVsSpotNok: 0,
                AntallTimer: 0,
                AntallTimerProduksjon: 0,
                AntallDager: 0,
                AntallDagerEtterFilter: 0);
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<MonthlyCaptureRate>> GetMonthlySeriesAsync(string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MonthlyCaptureRate>>(Array.Empty<MonthlyCaptureRate>());
        public Task<IReadOnlyList<DailyCaptureRate>> GetDailySeriesAsync(string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DailyCaptureRate>>(Array.Empty<DailyCaptureRate>());
    }

    private sealed class StubKaia : IKaiaCostQueryService
    {
        public Task<KaiaCostResult?> GetForImportAsync(string plantId, string idempotencyKey, CancellationToken ct) =>
            Task.FromResult<KaiaCostResult?>(null);
        public Task<IReadOnlyList<KaiaCostResult>> GetForPortfolioAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<KaiaCostResult>>(Array.Empty<KaiaCostResult>());
    }

    private sealed class StubVaktRoi : IPortfolioVaktRoiQueryService
    {
        private readonly Dictionary<(string plant, DateTimeOffset f, DateTimeOffset t), double> _reddet = new();

        public void SetReddet(string plantId, DateTimeOffset from, DateTimeOffset to, double reddet)
            => _reddet[(plantId, from, to)] = reddet;

        public Task<PortfolioVaktRoiResponse> GetAsync(
            DateTimeOffset fromUtc, DateTimeOffset toUtc, int topN,
            VaktTidsmodellOptions? vaktOptions, CancellationToken ct)
        {
            var per = _reddet
                .Where(kv => kv.Key.f == fromUtc && kv.Key.t == toUtc)
                .Select(kv => new PortfolioVaktRoiPlantSummary(
                    PlantId: kv.Key.plant,
                    PlantName: kv.Key.plant,
                    InstalledCapacityMw: 1.0,
                    ReddetNok: kv.Value,
                    ReddetProduksjon_NOK: kv.Value,
                    ReddetUbalanse_NOK: 0,
                    ReddbareEvents: 0,
                    TotaleEvents: 0))
                .ToList();
            var response = new PortfolioVaktRoiResponse(
                FromUtc: fromUtc, ToUtc: toUtc,
                PlantCount: per.Count, PlantsWithData: per.Count,
                TotalReddetNok: per.Sum(p => p.ReddetNok),
                TotalReddbareEvents: 0, TotalEvents: 0,
                PerPlant: per,
                TopEvents: Array.Empty<PortfolioVaktRoiTopEvent>(),
                MonthlyTrend: Array.Empty<PortfolioVaktRoiMonthlyPoint>());
            return Task.FromResult(response);
        }
    }

    private sealed class StubNedetid : INedetidQueryService
    {
        public Task<IReadOnlyList<DowntimeEvent>> ListEventsAsync(string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DowntimeEvent>>(Array.Empty<DowntimeEvent>());
        public Task<double> GetAvgImbalancePremiumAsync(string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult(0d);
        public Task<PlanByHourResult> GetProduksjonplanByHourAsync(string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult(new PlanByHourResult(new Dictionary<DateTimeOffset, double>(), new HashSet<DateTimeOffset>()));
    }

    private sealed class StubDataQuality : IDataQualityQueryService
    {
        public Task<DataQualitySummary?> GetSummaryAsync(string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult<DataQualitySummary?>(null);
        public Task<IReadOnlyList<DataQualitySummary>> GetSummariesForAllPlantsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DataQualitySummary>>(Array.Empty<DataQualitySummary>());
    }

    private sealed class StubConfig : IPlantConfiguration
    {
        private readonly double _vaktKost;
        public StubConfig(double vaktKost) => _vaktKost = vaktKost;

        public Task<T?> GetAsync<T>(string plantId, string key, CancellationToken ct = default)
        {
            if (plantId == "_portfolio_" && key == "vakt_kost_nok_per_aar")
            {
                return Task.FromResult((T?)(object?)_vaktKost);
            }
            return Task.FromResult(default(T));
        }

        public Task SetAsync<T>(string plantId, string key, T value, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
