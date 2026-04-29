using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Reporting.Nedetid;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Nedetid;

/// <summary>
/// Tester <see cref="OverflowQueryService"/> mot stub-repositorier. Repository-
/// kontraktene er ekstremt smale så stubber gir bedre signal enn EF in-memory
/// (som ikke matcher Postgres-oppførselen for snake_case + global query
/// filters). De ekte EF-implementasjonene har dedikerte integrasjonstester
/// mot Postgres ved behov.
/// </summary>
public class OverflowQueryServiceTests
{
    private const string PlantId = "drivdal";
    private const string OverflowSignalId = "DRIVDAL_INNTAK_NIVA_OVERLOP_VF_PV";

    private static OverflowQueryService Build(
        Dictionary<SignalRole, string?> rolemap,
        IReadOnlyList<ScadaSample> samples)
    {
        var signalMaps = new StubSignalMapRepository(PlantId, rolemap);
        var sampleRepo = new StubScadaSampleRepository(samples);
        return new OverflowQueryService(signalMaps, sampleRepo, NullLogger<OverflowQueryService>.Instance);
    }

    private static DateTimeOffset T(int day, int hour) =>
        new(2025, 2, day, hour, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HasOverflowTagAsync_FalskUtenTag()
    {
        var svc = Build(rolemap: new(), samples: Array.Empty<ScadaSample>());
        (await svc.HasOverflowTagAsync(PlantId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task HasOverflowTagAsync_TrueMedTag()
    {
        var svc = Build(
            rolemap: new() { [SignalRole.OverflowFlow] = OverflowSignalId },
            samples: Array.Empty<ScadaSample>());
        (await svc.HasOverflowTagAsync(PlantId, default)).Should().BeTrue();
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_UtenTag_DataAvailableFalse()
    {
        var svc = Build(rolemap: new(), samples: Array.Empty<ScadaSample>());
        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().BeEmpty();
        ds.DataAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_TagMenIngenSamples_DataAvailableFalse()
    {
        // Tag finnes, men ingen samples er importert i perioden.
        // Skal flagges som missing data, ikke "ingen overløp".
        var svc = Build(
            rolemap: new() { [SignalRole.OverflowFlow] = OverflowSignalId },
            samples: Array.Empty<ScadaSample>());
        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().BeEmpty();
        ds.DataAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_SamplesUnderTerskelTeller_DataAvailableTrue()
    {
        // Bare 0-verdier i perioden = "kjent ingen overløp" → dataAvailable = true,
        // overflowHours tom.
        var samples = new List<ScadaSample>
        {
            new(PlantId, OverflowSignalId, T(1, 0), 0.0, 0),
            new(PlantId, OverflowSignalId, T(1, 1), 0.0, 0),
        };
        var svc = Build(
            rolemap: new() { [SignalRole.OverflowFlow] = OverflowSignalId },
            samples: samples);
        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().BeEmpty();
        ds.DataAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_ReturnererTimerMedOverflow()
    {
        var samples = new List<ScadaSample>
        {
            new(PlantId, OverflowSignalId, T(1, 8), 0.0, 0),       // ingen overløp
            new(PlantId, OverflowSignalId, T(1, 9), 0.0005, 0),    // under terskel
            new(PlantId, OverflowSignalId, T(1, 10), 0.5, 0),      // overløp
            new(PlantId, OverflowSignalId, T(1, 11), 1.2, 0),      // overløp
            new(PlantId, OverflowSignalId, T(1, 12), null, 2),     // null/bad → ignoreres
            new(PlantId, OverflowSignalId, T(1, 13), 2.0, 0),      // overløp
        };

        var svc = Build(
            rolemap: new() { [SignalRole.OverflowFlow] = OverflowSignalId },
            samples: samples);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);

        ds.OverflowHours.Should().BeEquivalentTo(new[] { T(1, 10), T(1, 11), T(1, 13) });
        ds.DataAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_RespekterTerskel()
    {
        // OverflowQueryService.OverflowThresholdM3PerS = 0.001. Verdier =
        // terskelen ekskluderes; verdier rett over inkluderes.
        var samples = new List<ScadaSample>
        {
            new(PlantId, OverflowSignalId, T(1, 0), OverflowQueryService.OverflowThresholdM3PerS, 0),
            new(PlantId, OverflowSignalId, T(1, 1), OverflowQueryService.OverflowThresholdM3PerS + 0.0001, 0),
        };

        var svc = Build(
            rolemap: new() { [SignalRole.OverflowFlow] = OverflowSignalId },
            samples: samples);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().BeEquivalentTo(new[] { T(1, 1) });
        ds.DataAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_TrunkererTilTimeStart()
    {
        var samples = new List<ScadaSample>
        {
            new(PlantId, OverflowSignalId, new DateTimeOffset(2025, 2, 1, 10, 23, 17, TimeSpan.Zero), 1.0, 0),
        };

        var svc = Build(
            rolemap: new() { [SignalRole.OverflowFlow] = OverflowSignalId },
            samples: samples);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().ContainSingle().Which.Should().Be(T(1, 10));
    }

    private sealed class StubSignalMapRepository : ISignalMapRepository
    {
        private readonly string _plantId;
        private readonly Dictionary<SignalRole, string?> _roleMap;

        public StubSignalMapRepository(string plantId, Dictionary<SignalRole, string?> roleMap)
        {
            _plantId = plantId;
            _roleMap = roleMap;
        }

        public Task<string?> GetSignalIdForRoleAsync(string plantId, SignalRole role, CancellationToken ct)
        {
            if (!string.Equals(plantId, _plantId, StringComparison.Ordinal))
            {
                return Task.FromResult<string?>(null);
            }
            return Task.FromResult(_roleMap.TryGetValue(role, out var id) ? id : null);
        }

        public Task<IReadOnlyList<SignalMap>> ListForPlantAsync(string plantId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<SignalMap?> GetAsync(string plantId, string signalId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task UpsertAsync(SignalMap signalMap, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    private sealed class StubScadaSampleRepository : IScadaSampleRepository
    {
        private readonly IReadOnlyList<ScadaSample> _samples;

        public StubScadaSampleRepository(IReadOnlyList<ScadaSample> samples)
        {
            _samples = samples;
        }

        public Task<IReadOnlyList<ScadaSample>> ListAsync(
            string plantId, IReadOnlyCollection<string> signalIds,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            var ids = signalIds.ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<ScadaSample> result = _samples
                .Where(s => string.Equals(s.AssetId, plantId, StringComparison.Ordinal)
                    && ids.Contains(s.SignalId)
                    && s.TimeUtc >= fromUtc && s.TimeUtc < toUtc)
                .ToList();
            return Task.FromResult(result);
        }

        public Task<int> BulkInsertAsync(IReadOnlyCollection<ScadaSample> samples, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<int> DeleteOlderThanAsync(string plantId, DateTimeOffset cutoffUtc, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<int> DeleteAllAsync(CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
