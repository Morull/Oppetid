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

    /// <summary>
    /// Default-bygger: konfigurerer Drivdal med én terminal-dam og overflow-tagen
    /// fra <c>rolemap</c>. Tester for kaskade-scenarier bruker
    /// <see cref="BuildWithDams"/> direkte.
    /// </summary>
    private static OverflowQueryService Build(
        Dictionary<SignalRole, string?> rolemap,
        IReadOnlyList<ScadaSample> samples,
        OverflowMode overflowMode = OverflowMode.NativeTag,
        Dam? terminalDamOverride = null)
    {
        return BuildWithDams(
            terminalDam: terminalDamOverride ?? new Dam(PlantId, "drivdal_main", "Drivdal", 1, true, null, null, null),
            allDams: null,
            rolemap: rolemap,
            terminalRolemap: rolemap, // alle overflow-tags hører til terminal-dam i én-dam-anlegg
            samples: samples,
            overflowMode: overflowMode);
    }

    private static OverflowQueryService BuildWithDams(
        Dam? terminalDam,
        IReadOnlyList<Dam>? allDams,
        Dictionary<SignalRole, string?> rolemap,
        Dictionary<SignalRole, string?> terminalRolemap,
        IReadOnlyList<ScadaSample> samples,
        OverflowMode overflowMode = OverflowMode.NativeTag)
    {
        var signalMaps = new StubSignalMapRepository(PlantId, rolemap, terminalDam?.DamId, terminalRolemap);
        var sampleRepo = new StubScadaSampleRepository(samples);
        var damRepo = new StubDamRepository(terminalDam, allDams);
        var overflowConfig = new StubPlantOverflowConfigProvider(PlantId, overflowMode);
        return new OverflowQueryService(signalMaps, sampleRepo, damRepo, overflowConfig,
            NullLogger<OverflowQueryService>.Instance);
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
            new(PlantId, OverflowSignalId, T(1, 9), 0.3, 0),       // under terskel (0.5 m³/s)
            new(PlantId, OverflowSignalId, T(1, 10), 0.6, 0),      // overløp
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

    // -------- Kaskade-scenarier (Spec KASKADE-DAMMER) --------

    [Fact]
    public async Task GetOverflowDatasetAsync_Kaskade_OverflowPaTerminalDam_Telles()
    {
        // Haukland-lignende: 4 dammer, Stemmevatn er terminal. Overløps-tag på
        // Stemmevatn returneres av GetByPlantDamAndRoleAsync når damId matcher.
        const string terminalSignal = "HAUKLAND_STEMMEVT_KONTROLL_MAG_OVLOP_PV";
        var stolsvt = new Dam(PlantId, "haukland_stolsvt", "Stølsvatn", 1, false, null, null, null);
        var stemmevt = new Dam(PlantId, "haukland_stemmevt", "Stemmevatn", 3, true, null, null, null);

        var samples = new List<ScadaSample>
        {
            new(PlantId, terminalSignal, T(1, 5), 0.6, 0),  // over terskel (0.5 m³/s)
            new(PlantId, terminalSignal, T(1, 6), 1.0, 0),
        };

        var svc = BuildWithDams(
            terminalDam: stemmevt,
            allDams: new[] { stolsvt, stemmevt },
            rolemap: new() { [SignalRole.OverflowFlow] = terminalSignal },
            terminalRolemap: new() { [SignalRole.OverflowFlow] = terminalSignal },
            samples: samples);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().BeEquivalentTo(new[] { T(1, 5), T(1, 6) });
        ds.DataAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_Kaskade_OverflowPaaOvreDam_Ignoreres()
    {
        // Stølsvatn (øvre dam) har overflow-tag — men siden den IKKE er terminal
        // skal den IKKE telles. Stemmevatn (terminal) har ingen overflow-rader.
        // Resultat: 0 timer overflow, men DataAvailable=false fordi terminal-tag
        // mangler eller ikke har samples.
        var stolsvt = new Dam(PlantId, "haukland_stolsvt", "Stølsvatn", 1, false, null, null, null);
        var stemmevt = new Dam(PlantId, "haukland_stemmevt", "Stemmevatn", 3, true, null, null, null);

        // terminalRolemap er tom → GetByPlantDamAndRoleAsync(stemmevt, OverflowFlow)
        // returnerer tom liste. Overflow-data fra Stølsvatn er irrelevant.
        var samples = new List<ScadaSample>
        {
            new(PlantId, "HAUKLAND_STOLSVT_KONTROLL_MAG_OVLOP_PV", T(1, 5), 5.0, 0),
        };

        var svc = BuildWithDams(
            terminalDam: stemmevt,
            allDams: new[] { stolsvt, stemmevt },
            rolemap: new() { [SignalRole.OverflowFlow] = "HAUKLAND_STOLSVT_KONTROLL_MAG_OVLOP_PV" },
            terminalRolemap: new(), // ingen overflow-tag på terminal-dam i denne testen
            samples: samples);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().BeEmpty();
        ds.DataAvailable.Should().BeFalse(); // ingen overflow-tag på terminal → flagger missing
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_AnleggUtenTerminalDam_DataAvailableFalse()
    {
        // Dataintegritets-feil: backfill skal garantere én terminal-dam per plant,
        // men hvis noen manuelt sletter den, returnerer service tom + missing.
        var svc = BuildWithDams(
            terminalDam: null,
            allDams: Array.Empty<Dam>(),
            rolemap: new() { [SignalRole.OverflowFlow] = OverflowSignalId },
            terminalRolemap: new() { [SignalRole.OverflowFlow] = OverflowSignalId },
            samples: new List<ScadaSample>
            {
                new(PlantId, OverflowSignalId, T(1, 5), 0.5, 0),
            });

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().BeEmpty();
        ds.DataAvailable.Should().BeFalse();
    }

    // ──────── LevelProxy-modus (Ørsdalen-mønster) ────────────────────────────

    [Fact]
    public async Task GetOverflowDatasetAsync_LevelProxy_LevelOverHrvOgTerskel_GirOverflow()
    {
        // HRV = 100.00 moh, terskel = 10 cm → overflow når level > 100.10
        var dam = new Dam(PlantId, "drivdal_main", "Drivdal", 1, true,
            HrvMoh: 100.00, LrvMoh: null, VolumeMm3: null, OverflowProxyThresholdCm: 10);

        var samples = new List<ScadaSample>
        {
            new(PlantId, "LEVEL_TAG", T(1, 0), 99.95, 0),  // under HRV → ikke overflow
            new(PlantId, "LEVEL_TAG", T(1, 1), 100.05, 0), // 5cm over HRV, under terskel
            new(PlantId, "LEVEL_TAG", T(1, 2), 100.15, 0), // 15cm over HRV → overflow
            new(PlantId, "LEVEL_TAG", T(1, 3), 100.50, 0), // 50cm over HRV → overflow
        };

        var svc = Build(
            rolemap: new() { [SignalRole.UpstreamLevel] = "LEVEL_TAG" },
            samples: samples,
            overflowMode: OverflowMode.LevelProxy,
            terminalDamOverride: dam);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.DataAvailable.Should().BeTrue();
        ds.OverflowHours.Should().BeEquivalentTo(new[] { T(1, 2), T(1, 3) });
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_LevelProxy_HrvIkkeSatt_DataAvailableFalse()
    {
        // LevelProxy-modus konfigurert, men drifts-leder har ikke fylt inn HRV ennå.
        // Skal returnere tomt sett + DataAvailable=false slik at brukeren ser at
        // konfigurasjon mangler.
        var dam = new Dam(PlantId, "drivdal_main", "Drivdal", 1, true,
            HrvMoh: null, LrvMoh: null, VolumeMm3: null, OverflowProxyThresholdCm: 10);

        var svc = Build(
            rolemap: new() { [SignalRole.UpstreamLevel] = "LEVEL_TAG" },
            samples: new List<ScadaSample>
            {
                new(PlantId, "LEVEL_TAG", T(1, 0), 99.95, 0),
            },
            overflowMode: OverflowMode.LevelProxy,
            terminalDamOverride: dam);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().BeEmpty();
        ds.DataAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_LevelProxy_IngenLevelTag_DataAvailableFalse()
    {
        var dam = new Dam(PlantId, "drivdal_main", "Drivdal", 1, true,
            HrvMoh: 100.00, LrvMoh: null, VolumeMm3: null, OverflowProxyThresholdCm: 10);

        var svc = Build(
            rolemap: new(), // ingen UpstreamLevel mappet
            samples: Array.Empty<ScadaSample>(),
            overflowMode: OverflowMode.LevelProxy,
            terminalDamOverride: dam);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().BeEmpty();
        ds.DataAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task HasOverflowTagAsync_LevelProxy_ReturnererTrueSelv_UtenNativeTag()
    {
        // I LevelProxy-modus regnes proxy-konfigurasjonen som "har overflow-detektering",
        // så UI ikke viser "data missing"-advarsel.
        var svc = Build(
            rolemap: new(), // ingen native overflow-tag
            samples: Array.Empty<ScadaSample>(),
            overflowMode: OverflowMode.LevelProxy);

        (await svc.HasOverflowTagAsync(PlantId, default)).Should().BeTrue();
    }

    // ──────── ProductionStateProxy-modus (Stølskraft-mønster) ────────────────

    [Fact]
    public async Task GetOverflowDatasetAsync_ProductionStateProxy_GenPOverTerskel_GirOverflow()
    {
        const string GenPTag = "STOLSKRAFT_G1_GEN_P_PV";
        var samples = new List<ScadaSample>
        {
            new(PlantId, GenPTag, T(1, 0), 0.0, 0),    // ikke i produksjon
            new(PlantId, GenPTag, T(1, 1), 0.5, 0),    // under 1 kW-terskel
            new(PlantId, GenPTag, T(1, 2), 100.0, 0),  // i produksjon
            new(PlantId, GenPTag, T(1, 3), 500.0, 0),  // i produksjon
        };

        var svc = Build(
            rolemap: new() { [SignalRole.GeneratorActivePower] = GenPTag },
            samples: samples,
            overflowMode: OverflowMode.ProductionStateProxy);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.DataAvailable.Should().BeTrue();
        ds.OverflowHours.Should().BeEquivalentTo(new[] { T(1, 2), T(1, 3) });
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_ProductionStateProxy_IngenGenPTag_DataAvailableFalse()
    {
        var svc = Build(
            rolemap: new(), // ingen GeneratorActivePower mappet
            samples: Array.Empty<ScadaSample>(),
            overflowMode: OverflowMode.ProductionStateProxy);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.OverflowHours.Should().BeEmpty();
        ds.DataAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task GetOverflowDatasetAsync_ProductionStateProxy_AlleHourerProduksjon0_DataAvailableTrue_MenTomt()
    {
        // Anlegget har GEN_P-tag og samples er importert, men plantet produserer ikke
        // i perioden (drikkevannskraftverk med lav drift). DataAvailable=true,
        // OverflowHours tomt → Vakt-ROI får 0 NOK med forklaring "ingen produksjon".
        const string GenPTag = "STOLSKRAFT_G1_GEN_P_PV";
        var samples = new List<ScadaSample>
        {
            new(PlantId, GenPTag, T(1, 0), 0.0, 0),
            new(PlantId, GenPTag, T(1, 1), 0.0, 0),
        };

        var svc = Build(
            rolemap: new() { [SignalRole.GeneratorActivePower] = GenPTag },
            samples: samples,
            overflowMode: OverflowMode.ProductionStateProxy);

        var ds = await svc.GetOverflowDatasetAsync(PlantId, T(1, 0), T(2, 0), default);
        ds.DataAvailable.Should().BeTrue();
        ds.OverflowHours.Should().BeEmpty();
    }

    [Fact]
    public async Task HasOverflowTagAsync_ProductionStateProxy_ReturnererTrue()
    {
        var svc = Build(
            rolemap: new(),
            samples: Array.Empty<ScadaSample>(),
            overflowMode: OverflowMode.ProductionStateProxy);

        (await svc.HasOverflowTagAsync(PlantId, default)).Should().BeTrue();
    }

    private sealed class StubSignalMapRepository : ISignalMapRepository
    {
        private readonly string _plantId;
        private readonly Dictionary<SignalRole, string?> _roleMap;
        private readonly string? _terminalDamId;
        private readonly Dictionary<SignalRole, string?> _terminalRolemap;

        public StubSignalMapRepository(
            string plantId,
            Dictionary<SignalRole, string?> roleMap,
            string? terminalDamId = null,
            Dictionary<SignalRole, string?>? terminalRolemap = null)
        {
            _plantId = plantId;
            _roleMap = roleMap;
            _terminalDamId = terminalDamId;
            _terminalRolemap = terminalRolemap ?? roleMap;
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

        /// <summary>
        /// Returnerer signal hvis (plantId, damId, role) matcher konfigurasjonen:
        /// <list type="bullet">
        ///   <item>For terminal-dam (damId = <c>_terminalDamId</c>): bruk _terminalRolemap.</item>
        ///   <item>For damId=null (generator/sentral-tags): bruk _roleMap.</item>
        ///   <item>Ellers (øvre kaskade-dammer): tom liste.</item>
        /// </list>
        /// </summary>
        public Task<IReadOnlyList<SignalMap>> GetByPlantDamAndRoleAsync(
            string plantId, string? damId, SignalRole role, CancellationToken ct)
        {
            if (!string.Equals(plantId, _plantId, StringComparison.Ordinal))
            {
                return Task.FromResult<IReadOnlyList<SignalMap>>(Array.Empty<SignalMap>());
            }
            // For terminal-dam: returner signal fra _terminalRolemap
            if (damId == _terminalDamId
                && _terminalRolemap.TryGetValue(role, out var terminalId) && terminalId is not null)
            {
                var sm = new SignalMap(plantId, terminalId, terminalId, "m3/s", role, true, true, damId);
                return Task.FromResult<IReadOnlyList<SignalMap>>(new[] { sm });
            }
            // For damId=null (generator-tags som GeneratorActivePower): bruk hovedroleMap
            if (damId is null
                && _roleMap.TryGetValue(role, out var plantId2) && plantId2 is not null)
            {
                var sm = new SignalMap(plantId, plantId2, plantId2, "kW", role, true, true, null);
                return Task.FromResult<IReadOnlyList<SignalMap>>(new[] { sm });
            }
            return Task.FromResult<IReadOnlyList<SignalMap>>(Array.Empty<SignalMap>());
        }
    }

    /// <summary>
    /// Stub for IDamRepository — returnerer den konfigurerte terminal-dammen
    /// (eller null hvis testen ønsker å simulere et anlegg uten terminal-dam).
    /// </summary>
    private sealed class StubDamRepository : IDamRepository
    {
        private readonly Dam? _terminalDam;
        private readonly IReadOnlyList<Dam> _allDams;

        public StubDamRepository(Dam? terminalDam, IReadOnlyList<Dam>? allDams)
        {
            _terminalDam = terminalDam;
            _allDams = allDams ?? (terminalDam is null ? Array.Empty<Dam>() : new[] { terminalDam });
        }

        public Task<IReadOnlyList<Dam>> GetForPlantAsync(string plantId, CancellationToken ct)
            => Task.FromResult(_allDams);
        public Task<Dam?> GetTerminalDamAsync(string plantId, CancellationToken ct)
            => Task.FromResult(_terminalDam);
        public Task AddAsync(Dam dam, CancellationToken ct) => throw new NotImplementedException();
        public Task UpdateAsync(Dam dam, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteAsync(string plantId, string damId, CancellationToken ct) => throw new NotImplementedException();
    }

    /// <summary>
    /// Stub for IPlantOverflowConfigProvider — returnerer konfigurert modus for
    /// PlantId, NativeTag for andre plant-IDs (matcher prod-default).
    /// </summary>
    private sealed class StubPlantOverflowConfigProvider : IPlantOverflowConfigProvider
    {
        private readonly string _plantId;
        private readonly OverflowMode _mode;

        public StubPlantOverflowConfigProvider(string plantId, OverflowMode mode)
        {
            _plantId = plantId;
            _mode = mode;
        }

        public Task<OverflowMode> GetOverflowModeAsync(string plantId, CancellationToken ct)
        {
            var mode = string.Equals(plantId, _plantId, StringComparison.Ordinal)
                ? _mode
                : OverflowMode.NativeTag;
            return Task.FromResult(mode);
        }
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
