using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Reporting.Effektivitet;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Effektivitet;

/// <summary>
/// Unit-tester for <see cref="EffektivitetQueryService"/>:
///   – Sweet-spot beregnes algoritmisk fra dataen, ikke hardkodet
///   – Mangel på tags eller samples gir DataMissing-flagg
///   – Spesifikt vannforbruk = sum(Q × s) / sum(P × t) der konstantene avhenger
///     av om vi leser 15-min (×900 / ×0.25) eller hourly fallback (×3600 / ×1)
///   – Klassifisering (Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md):
///     * Genuine: η ≥ GenuineEtaFloorPct → teller i alle snitt-aggregat
///     * Transition: P ≥ terskel men η under gulvet → synlig i Punkter,
///       teller IKKE i snitt-aggregat, bins eller sweet-spot
///   – Anlegg-uavhengig: stub'er kan gi data for vilkårlig plant-id
/// </summary>
public class EffektivitetQueryServiceTests
{
    private const string Plant = "test-plant";
    private const string PowerSig = "POWER";
    private const string EtaSig = "ETA";
    private const string FlowSig = "FLOW";

    /// <summary>
    /// Bygger en service der <paramref name="samples"/> ligger i hourly-repoet
    /// og fine-repoet er tomt. Eksisterende tester bruker hourly-fallback —
    /// integrasjons-konstantene blir ×1 t / ×3600 s, som matcher tidligere assert-verdier.
    /// </summary>
    private static EffektivitetQueryService BuildHourly(
        Dictionary<SignalRole, string?> roleMap,
        IReadOnlyList<ScadaSample> samples)
    {
        return new EffektivitetQueryService(
            new StubSignalMapRepository(Plant, roleMap),
            new StubSampleFineRepository(Array.Empty<ScadaSample>()),
            new StubSampleRepository(samples),
            NullLogger<EffektivitetQueryService>.Instance);
    }

    /// <summary>
    /// Bygger en service der samples ligger i FINE-repoet (15-min-pipelinen).
    /// Integrasjons-konstantene blir ×0.25 t / ×900 s.
    /// </summary>
    private static EffektivitetQueryService BuildFine(
        Dictionary<SignalRole, string?> roleMap,
        IReadOnlyList<ScadaSample> samples)
    {
        return new EffektivitetQueryService(
            new StubSignalMapRepository(Plant, roleMap),
            new StubSampleFineRepository(samples),
            new StubSampleRepository(Array.Empty<ScadaSample>()),
            NullLogger<EffektivitetQueryService>.Instance);
    }

    private static DateTimeOffset T(int hour) =>
        new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).AddHours(hour);

    private static DateTimeOffset Q(int quarter) =>
        new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(quarter * 15);

    private static IReadOnlyList<ScadaSample> Triple(int hour, double p, double eta, double q) =>
    [
        new ScadaSample(Plant, PowerSig, T(hour), p, 0),
        new ScadaSample(Plant, EtaSig,   T(hour), eta, 0),
        new ScadaSample(Plant, FlowSig,  T(hour), q, 0),
    ];

    private static IReadOnlyList<ScadaSample> QuarterTriple(int quarter, double p, double eta, double q) =>
    [
        new ScadaSample(Plant, PowerSig, Q(quarter), p, 0),
        new ScadaSample(Plant, EtaSig,   Q(quarter), eta, 0),
        new ScadaSample(Plant, FlowSig,  Q(quarter), q, 0),
    ];

    private static Dictionary<SignalRole, string?> FullMap() => new()
    {
        [SignalRole.GeneratorActivePower] = PowerSig,
        [SignalRole.TurbineEfficiency] = EtaSig,
        [SignalRole.TurbineWaterFlow] = FlowSig,
    };

    [Fact]
    public async Task GetAsync_ManglerTags_DataMissingTrue()
    {
        var svc = BuildHourly(roleMap: new(), samples: Array.Empty<ScadaSample>());
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);
        resp.DataMissing.Should().BeTrue();
        resp.ProduksjonsTimer.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_MangerSamples_DataMissingTrue()
    {
        var svc = BuildHourly(FullMap(), samples: Array.Empty<ScadaSample>());
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);
        resp.DataMissing.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_AlleEffekterUnderTerskel_GirNullProduksjonMenIkkeMissing()
    {
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 10, eta: 80, q: 0.1));
        samples.AddRange(Triple(1, p: 20, eta: 78, q: 0.1));
        samples.AddRange(Triple(2, p: 30, eta: 75, q: 0.1));

        var svc = BuildHourly(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.DataMissing.Should().BeFalse();
        resp.ProduksjonsTimer.Should().Be(0);
        resp.SnittEtaPct.Should().Be(0);
        resp.Punkter.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAsync_FullProduksjon_BeregnerRiktigSnittOgSweetSpot()
    {
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 1850, eta: 92, q: 1.5));
        samples.AddRange(Triple(1, p: 1900, eta: 93, q: 1.5));
        samples.AddRange(Triple(2, p: 1950, eta: 91, q: 1.5));
        samples.AddRange(Triple(3, p: 1050, eta: 80, q: 0.9));
        samples.AddRange(Triple(4, p: 1100, eta: 82, q: 0.9));
        samples.AddRange(Triple(5, p: 1150, eta: 81, q: 0.9));

        var svc = BuildHourly(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.DataMissing.Should().BeFalse();
        resp.ProduksjonsTimer.Should().Be(6);
        resp.SnittEtaPct.Should().BeApproximately((92 + 93 + 91 + 80 + 82 + 81) / 6.0, 0.001);

        resp.SweetSpotEffektKw.Should().Be(1900);
        resp.SweetSpotEtaPct.Should().BeApproximately(92.0, 0.001);

        resp.Bins.Should().HaveCount(2);
        resp.Bins[0].EffektKwStart.Should().Be(1000);
        resp.Bins[1].EffektKwStart.Should().Be(1800);

        // Alle punktene skal være Genuine (η ≥ 50).
        resp.Punkter.Should().HaveCount(6);
        resp.Punkter.Should().OnlyContain(p => p.Klassifisering == PunktKlassifisering.Genuine);
    }

    [Fact]
    public async Task GetAsync_HourlyFallback_SpesifiktVannforbruk_BeregnesKorrekt()
    {
        // Hourly-fallback: T0: P=1000, Q=1.0 → kWh=1000, m³=3600
        //                   T1: P=2000, Q=1.5 → kWh=2000, m³=5400
        // Total: 3000 kWh, 9000 m³, SVF = 3.0 m³/kWh
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 1000, eta: 85, q: 1.0));
        samples.AddRange(Triple(1, p: 2000, eta: 90, q: 1.5));

        var svc = BuildHourly(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.SnittSpesifiktVannforbrukM3PerKwh.Should().BeApproximately(3.0, 0.001);
        resp.TotalProduksjonKwh.Should().Be(3000);
    }

    [Fact]
    public async Task GetAsync_FineSource_IntegrasjonBruker0_25TimePer15MinIntervall()
    {
        // 15-min-pipelinen: 4 kvarter (1 time) på P=2000 kW skal gi 2000 kWh.
        // Q=2.0 m³/s × 900 s/kvarter × 4 kvarter = 7200 m³.
        // SVF = 7200 / 2000 = 3.6 m³/kWh.
        var samples = new List<ScadaSample>();
        for (var i = 0; i < 4; i++)
        {
            samples.AddRange(QuarterTriple(i, p: 2000, eta: 88, q: 2.0));
        }

        var svc = BuildFine(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, Q(0), Q(8), default);

        resp.TotalProduksjonKwh.Should().BeApproximately(2000, 0.001);
        resp.SnittSpesifiktVannforbrukM3PerKwh.Should().BeApproximately(3.6, 0.001);
        resp.ProduksjonsTimer.Should().Be(4); // 4 Genuine-kvarter
    }

    [Fact]
    public async Task GetAsync_SweetSpotKreverMinSamples_HopperOverTynneBins()
    {
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 1900, eta: 95, q: 1.5));
        samples.AddRange(Triple(1, p: 1050, eta: 80, q: 0.9));
        samples.AddRange(Triple(2, p: 1100, eta: 82, q: 0.9));
        samples.AddRange(Triple(3, p: 1150, eta: 81, q: 0.9));

        var svc = BuildHourly(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.SweetSpotEffektKw.Should().Be(1100);
        resp.SweetSpotEtaPct.Should().BeApproximately(81.0, 0.001);
    }

    [Fact]
    public async Task GetAsync_HopperOverTimerMedManglendeFelter()
    {
        var samples = new List<ScadaSample>
        {
            new(Plant, PowerSig, T(0), 1500, 0),
            new(Plant, EtaSig,   T(0), 90, 0),
            // Q mangler for T0
            new(Plant, PowerSig, T(1), 1500, 0),
            new(Plant, EtaSig,   T(1), 90, 0),
            new(Plant, FlowSig,  T(1), 1.2, 0),
        };

        var svc = BuildHourly(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.ProduksjonsTimer.Should().Be(1);
    }

    // ----- Klassifisering (NESTE-CHAT-EFFEKTIVITET-15MIN.md) ---------------

    [Fact]
    public async Task Klassifisering_EtaUnderGulv_FlaggesSomTransition()
    {
        // P over terskel (50 kW) men η under gulv (50 %) → Transition.
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 1500, eta: 30, q: 1.0));
        samples.AddRange(Triple(1, p: 1800, eta: 25, q: 1.2));

        var svc = BuildHourly(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.Punkter.Should().HaveCount(2);
        resp.Punkter.Should().OnlyContain(p => p.Klassifisering == PunktKlassifisering.Transition);
        // ProduksjonsTimer teller bare Genuine — derfor 0 her.
        resp.ProduksjonsTimer.Should().Be(0);
        resp.SnittEtaPct.Should().Be(0);
        resp.Bins.Should().BeEmpty();
        resp.DataMissing.Should().BeFalse(); // dataen FINNES, bare ikke Genuine
    }

    [Fact]
    public async Task Klassifisering_EtaPaaGulv_TellerSomGenuine()
    {
        // η = 50.0 % er på gulvet — skal regnes som Genuine (η >= 50).
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 1500, eta: 50.0, q: 1.0));

        var svc = BuildHourly(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.Punkter.Should().ContainSingle()
            .Which.Klassifisering.Should().Be(PunktKlassifisering.Genuine);
        resp.ProduksjonsTimer.Should().Be(1);
    }

    [Fact]
    public async Task Klassifisering_MixGenuineOgTransition_BareGenuineITellesIAggregat()
    {
        // 3 Genuine-intervaller (η 90, 88, 92) + 2 Transition (η 20, 35).
        // Snitt skal kun beregnes på Genuine: (90+88+92)/3 = 90.0.
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 1500, eta: 90, q: 1.0));
        samples.AddRange(Triple(1, p: 1600, eta: 88, q: 1.1));
        samples.AddRange(Triple(2, p: 1700, eta: 92, q: 1.2));
        samples.AddRange(Triple(3, p: 500,  eta: 20, q: 0.5)); // ramp-up
        samples.AddRange(Triple(4, p: 800,  eta: 35, q: 0.7)); // ramp-down

        var svc = BuildHourly(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.Punkter.Should().HaveCount(5);
        resp.Punkter.Count(p => p.Klassifisering == PunktKlassifisering.Genuine).Should().Be(3);
        resp.Punkter.Count(p => p.Klassifisering == PunktKlassifisering.Transition).Should().Be(2);

        // Aggregat-snitt baseres KUN på Genuine.
        resp.ProduksjonsTimer.Should().Be(3);
        resp.SnittEtaPct.Should().BeApproximately(90.0, 0.001);
        // Total produksjon = 1500 + 1600 + 1700 = 4800 kWh (hourly).
        resp.TotalProduksjonKwh.Should().BeApproximately(4800, 0.001);
        // Bins skal kun inneholde Genuine-effekter (1500, 1600, 1700 alle i bin 1400-1599 + 1600-1799).
        resp.Bins.Sum(b => b.Antall).Should().Be(3);
    }

    [Fact]
    public async Task Klassifisering_TransitionBidrarIkkeTilTotalProduksjon()
    {
        // 1 Genuine + 1 Transition, begge med samme P. Total skal kun reflektere Genuine.
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 2000, eta: 90, q: 1.5));
        samples.AddRange(Triple(1, p: 2000, eta: 30, q: 1.5));

        var svc = BuildHourly(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.TotalProduksjonKwh.Should().BeApproximately(2000, 0.001); // bare Genuine bidrar
    }

    // ----- Stubs ------------------------------------------------------------

    private sealed class StubSignalMapRepository : ISignalMapRepository
    {
        private readonly string _plantId;
        private readonly Dictionary<SignalRole, string?> _map;

        public StubSignalMapRepository(string plantId, Dictionary<SignalRole, string?> map)
        {
            _plantId = plantId;
            _map = map;
        }

        public Task<string?> GetSignalIdForRoleAsync(string plantId, SignalRole role, CancellationToken ct)
            => Task.FromResult(plantId == _plantId && _map.TryGetValue(role, out var id) ? id : null);

        public Task<IReadOnlyList<SignalMap>> ListForPlantAsync(string plantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<SignalMap?> GetAsync(string plantId, string signalId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task UpsertAsync(SignalMap signalMap, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<SignalMap>> GetByPlantDamAndRoleAsync(
            string plantId, string? damId, SignalRole role, CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubSampleRepository : IScadaSampleRepository
    {
        private readonly IReadOnlyList<ScadaSample> _samples;

        public StubSampleRepository(IReadOnlyList<ScadaSample> samples) { _samples = samples; }

        public Task<IReadOnlyList<ScadaSample>> ListAsync(
            string plantId, IReadOnlyCollection<string> signalIds,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            var ids = signalIds.ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<ScadaSample> result = _samples
                .Where(s => s.AssetId == plantId && ids.Contains(s.SignalId)
                    && s.TimeUtc >= fromUtc && s.TimeUtc < toUtc)
                .ToList();
            return Task.FromResult(result);
        }

        public Task<int> BulkInsertAsync(IReadOnlyCollection<ScadaSample> samples, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> DeleteOlderThanAsync(string plantId, DateTimeOffset cutoffUtc, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> DeleteAllAsync(CancellationToken ct)
            => throw new NotImplementedException();
    }

    private sealed class StubSampleFineRepository : IScadaSampleFineRepository
    {
        private readonly IReadOnlyList<ScadaSample> _samples;

        public StubSampleFineRepository(IReadOnlyList<ScadaSample> samples) { _samples = samples; }

        public Task<IReadOnlyList<ScadaSample>> ListAsync(
            string plantId, IReadOnlyCollection<string> signalIds,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            var ids = signalIds.ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<ScadaSample> result = _samples
                .Where(s => s.AssetId == plantId && ids.Contains(s.SignalId)
                    && s.TimeUtc >= fromUtc && s.TimeUtc < toUtc)
                .ToList();
            return Task.FromResult(result);
        }

        public Task<int> BulkInsertAsync(IReadOnlyCollection<ScadaSample> samples, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> DeleteOlderThanAsync(string plantId, DateTimeOffset cutoffUtc, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct)
            => throw new NotImplementedException();
        public Task<int> DeleteAllAsync(CancellationToken ct)
            => throw new NotImplementedException();
    }
}
