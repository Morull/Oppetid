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
///   – Spesifikt vannforbruk = sum(Q × 3600) / sum(P_kWh)
///   – Anlegg-uavhengig: stub'er kan gi data for vilkårlig plant-id
/// </summary>
public class EffektivitetQueryServiceTests
{
    private const string Plant = "test-plant";
    private const string PowerSig = "POWER";
    private const string EtaSig = "ETA";
    private const string FlowSig = "FLOW";

    private static EffektivitetQueryService Build(
        Dictionary<SignalRole, string?> roleMap,
        IReadOnlyList<ScadaSample> samples)
    {
        return new EffektivitetQueryService(
            new StubSignalMapRepository(Plant, roleMap),
            new StubSampleRepository(samples),
            NullLogger<EffektivitetQueryService>.Instance);
    }

    private static DateTimeOffset T(int hour) =>
        new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero).AddHours(hour);

    private static IReadOnlyList<ScadaSample> Triple(int hour, double p, double eta, double q) =>
    [
        new ScadaSample(Plant, PowerSig, T(hour), p, 0),
        new ScadaSample(Plant, EtaSig,   T(hour), eta, 0),
        new ScadaSample(Plant, FlowSig,  T(hour), q, 0),
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
        var svc = Build(roleMap: new(), samples: Array.Empty<ScadaSample>());
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);
        resp.DataMissing.Should().BeTrue();
        resp.ProduksjonsTimer.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_MangerSamples_DataMissingTrue()
    {
        var svc = Build(FullMap(), samples: Array.Empty<ScadaSample>());
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);
        resp.DataMissing.Should().BeTrue();
    }

    [Fact]
    public async Task GetAsync_AlleEffekterUnderTerskel_GirNullProduksjonMenIkkeMissing()
    {
        // 3 timer med P < 50 kW (terskelen). Datene finnes — derfor ikke "missing" —
        // men ingen kvalifiserende produksjons-timer.
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 10, eta: 80, q: 0.1));
        samples.AddRange(Triple(1, p: 20, eta: 78, q: 0.1));
        samples.AddRange(Triple(2, p: 30, eta: 75, q: 0.1));

        var svc = Build(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.DataMissing.Should().BeFalse();
        resp.ProduksjonsTimer.Should().Be(0);
        resp.SnittEtaPct.Should().Be(0);
    }

    [Fact]
    public async Task GetAsync_FullProduksjon_BeregnerRiktigSnittOgSweetSpot()
    {
        // Lag 6 produksjons-timer. Sweet-spot skal komme på effekt-bin'en med
        // høyest snitt-eta. Bin-bredde = 200 kW (default i query-service).
        //   Bin 1800-1999 (mid 1900): 3 timer @ eta 92, 93, 91  → snitt 92.0
        //   Bin 1000-1199 (mid 1100): 3 timer @ eta 80, 82, 81  → snitt 81.0
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 1850, eta: 92, q: 1.5));
        samples.AddRange(Triple(1, p: 1900, eta: 93, q: 1.5));
        samples.AddRange(Triple(2, p: 1950, eta: 91, q: 1.5));
        samples.AddRange(Triple(3, p: 1050, eta: 80, q: 0.9));
        samples.AddRange(Triple(4, p: 1100, eta: 82, q: 0.9));
        samples.AddRange(Triple(5, p: 1150, eta: 81, q: 0.9));

        var svc = Build(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.DataMissing.Should().BeFalse();
        resp.ProduksjonsTimer.Should().Be(6);
        resp.SnittEtaPct.Should().BeApproximately((92 + 93 + 91 + 80 + 82 + 81) / 6.0, 0.001);

        // Sweet-spot er bin 1800-1999, midtpunkt 1900 kW, snitt η = 92.0.
        resp.SweetSpotEffektKw.Should().Be(1900);
        resp.SweetSpotEtaPct.Should().BeApproximately(92.0, 0.001);

        // Bins skal inneholde begge — sortert på effekt-start.
        resp.Bins.Should().HaveCount(2);
        resp.Bins[0].EffektKwStart.Should().Be(1000);
        resp.Bins[1].EffektKwStart.Should().Be(1800);
    }

    [Fact]
    public async Task GetAsync_SpesifiktVannforbruk_BeregnesKorrekt()
    {
        // To timer:
        //   T0: P=1000 kW, Q=1.0 m³/s → kWh=1000, m³=3600
        //   T1: P=2000 kW, Q=1.5 m³/s → kWh=2000, m³=5400
        // Total: kWh=3000, m³=9000. SVF = 9000 / 3000 = 3.0 m³/kWh
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 1000, eta: 85, q: 1.0));
        samples.AddRange(Triple(1, p: 2000, eta: 90, q: 1.5));

        var svc = Build(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.SnittSpesifiktVannforbrukM3PerKwh.Should().BeApproximately(3.0, 0.001);
        resp.TotalProduksjonKwh.Should().Be(3000);
    }

    [Fact]
    public async Task GetAsync_SweetSpotKreverMinSamples_HopperOverTynneBins()
    {
        // Bin 1800-1999 har bare 1 sample (under MinSamplesPerBin=3).
        // Bin 1000-1199 har 3 samples med litt lavere η.
        // Sweet-spot skal være bin 1000-1199 fordi den eneste 1900-kW-rad'en
        // er for tynn til å kvalifisere.
        var samples = new List<ScadaSample>();
        samples.AddRange(Triple(0, p: 1900, eta: 95, q: 1.5)); // alenestående høy η
        samples.AddRange(Triple(1, p: 1050, eta: 80, q: 0.9));
        samples.AddRange(Triple(2, p: 1100, eta: 82, q: 0.9));
        samples.AddRange(Triple(3, p: 1150, eta: 81, q: 0.9));

        var svc = Build(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.SweetSpotEffektKw.Should().Be(1100); // midt-punkt for 1000-1199
        resp.SweetSpotEtaPct.Should().BeApproximately(81.0, 0.001);
    }

    [Fact]
    public async Task GetAsync_HopperOverTimerMedManglendeFelter()
    {
        // Time 0 har bare P og η, mangler Q → skal ikke telles.
        // Time 1 er komplett.
        var samples = new List<ScadaSample>
        {
            new(Plant, PowerSig, T(0), 1500, 0),
            new(Plant, EtaSig,   T(0), 90, 0),
            // Q mangler for T0
            new(Plant, PowerSig, T(1), 1500, 0),
            new(Plant, EtaSig,   T(1), 90, 0),
            new(Plant, FlowSig,  T(1), 1.2, 0),
        };

        var svc = Build(FullMap(), samples);
        var resp = await svc.GetAsync(Plant, T(0), T(24), default);

        resp.ProduksjonsTimer.Should().Be(1);
    }

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
}
