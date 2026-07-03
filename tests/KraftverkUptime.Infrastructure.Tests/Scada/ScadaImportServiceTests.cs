using System.Text;
using FluentAssertions;
using KraftverkUptime.Core.DataCompleteness;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Scada;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests.Scada;

/// <summary>
/// SPEC-IMPORT-KONSOLIDERT-15MIN Endring B (hourly avledes fra 15-min ved
/// import) + Endring D (tag-minimering: samples uten StoreSamples=true
/// droppes) + Endring C (data_imports logges som source_type="scada").
/// </summary>
public class ScadaImportServiceTests
{
    private static readonly DateTimeOffset H0 = new(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

    private static ScadaSample S(string signal, DateTimeOffset t, double? v, short q = 0)
        => new("drivdal", signal, t, v, q);

    // ---- AggregateFineToHourly (Endring B) --------------------------------

    [Fact]
    public void Aggregate_FireKvarter_GirSnittPaaTimestart()
    {
        var fine = new[]
        {
            S("TAG", H0, 10), S("TAG", H0.AddMinutes(15), 20),
            S("TAG", H0.AddMinutes(30), 30), S("TAG", H0.AddMinutes(45), 40),
        };

        var hourly = ScadaImportService.AggregateFineToHourly(fine);

        hourly.Should().HaveCount(1);
        hourly[0].TimeUtc.Should().Be(H0, "time-bucketens tidsstempel = timens start (:00)");
        hourly[0].Value.Should().Be(25, "avg(10,20,30,40)");
        hourly[0].Quality.Should().Be(0);
    }

    [Fact]
    public void Aggregate_DelvisTime_SnittAvTilgjengeligeKvarter()
    {
        var fine = new[] { S("TAG", H0.AddMinutes(15), 20), S("TAG", H0.AddMinutes(45), 40) };

        var hourly = ScadaImportService.AggregateFineToHourly(fine);

        hourly.Should().HaveCount(1);
        hourly[0].Value.Should().Be(30, "avg av de 2 tilgjengelige kvarterene");
    }

    [Fact]
    public void Aggregate_TimeMedBareNullVerdier_GirNullOgBadQuality()
    {
        var fine = new[] { S("TAG", H0, null, 2), S("TAG", H0.AddMinutes(15), null, 2) };

        var hourly = ScadaImportService.AggregateFineToHourly(fine);

        hourly.Should().HaveCount(1);
        hourly[0].Value.Should().BeNull();
        hourly[0].Quality.Should().Be(2);
    }

    [Fact]
    public void Aggregate_FlereSignalerOgTimer_GrupperesRiktig()
    {
        var fine = new[]
        {
            S("A", H0, 1), S("A", H0.AddMinutes(15), 3),
            S("A", H0.AddHours(1), 5),
            S("B", H0, 100),
        };

        var hourly = ScadaImportService.AggregateFineToHourly(fine);

        hourly.Should().HaveCount(3);
        hourly.Single(s => s.SignalId == "A" && s.TimeUtc == H0).Value.Should().Be(2);
        hourly.Single(s => s.SignalId == "A" && s.TimeUtc == H0.AddHours(1)).Value.Should().Be(5);
        hourly.Single(s => s.SignalId == "B").Value.Should().Be(100);
    }

    [Fact]
    public void Aggregate_DstDogn_UtcBucketing_IngenSaerbehandling()
    {
        // DST-vår i Europe/Oslo 2026: 29. mars (23-timers lokaldøgn). Parseren
        // har alt konvertert til UTC, så aggregering per UTC-time er upåvirket —
        // kvarter rundt overgangen havner i riktige UTC-buckets.
        var dst = new DateTimeOffset(2026, 3, 29, 0, 45, 0, TimeSpan.Zero);
        var fine = new[]
        {
            S("TAG", dst, 10),                    // 00:45 UTC → bucket 00
            S("TAG", dst.AddMinutes(15), 20),     // 01:00 UTC → bucket 01
            S("TAG", dst.AddMinutes(30), 30),     // 01:15 UTC → bucket 01
            S("TAG", dst.AddMinutes(90), 40),     // 02:15 UTC → bucket 02
        };

        var hourly = ScadaImportService.AggregateFineToHourly(fine);

        hourly.Should().HaveCount(3);
        hourly.Single(s => s.TimeUtc.Hour == 0).Value.Should().Be(10);
        hourly.Single(s => s.TimeUtc.Hour == 1).Value.Should().Be(25);
        hourly.Single(s => s.TimeUtc.Hour == 2).Value.Should().Be(40);
    }

    [Fact]
    public void Aggregate_ErDeterministisk_ReimportGirIdentiskeAggregater()
    {
        // Idempotens (testplan §9.3): samme fine-innhold → identiske aggregat-
        // nøkler og -verdier. Sammen med BulkInsertAsyncs delete-then-insert-
        // upsert på (asset, signal, time) kan re-import ikke doble hourly.
        var fine = new[]
        {
            S("A", H0, 1), S("A", H0.AddMinutes(15), 3), S("B", H0.AddMinutes(30), 7),
        };

        var first = ScadaImportService.AggregateFineToHourly(fine);
        var second = ScadaImportService.AggregateFineToHourly(fine);

        second.Should().BeEquivalentTo(first, o => o.WithStrictOrdering());
    }

    // ---- Import-filter + hourly-avledning + logging (Endring B+C+D) -------

    [Fact]
    public async Task FineImport_FiltrererDeaktiverteOgUkjenteTags_OgAvlederHourly()
    {
        var harness = new Harness(signalMaps:
        [
            Map("DRIVDAL_G1_GEN_P_PV", storeSamples: true),
            Map("DRIVDAL_G1_TURB_VF_PV", storeSamples: true),
            Map("DRIVDAL_G1_GEN_COSPHI_PV", storeSamples: false), // deaktivert
            // DRIVDAL_UKJENT_TAG finnes ikke i signal_map i det hele tatt
        ]);

        var csv = BuildCsv(
            ["DRIVDAL_G1_GEN_P_PV", "DRIVDAL_G1_TURB_VF_PV", "DRIVDAL_G1_GEN_COSPHI_PV", "DRIVDAL_UKJENT_TAG"],
            start: new DateTime(2026, 6, 1, 10, 0, 0),
            stepMinutes: 15,
            rows: 8);

        var result = await harness.Service.ImportMasterCsvFineAsync(
            "drivdal", "dev-org", csv, CancellationToken.None);

        // 8 rader × 2 aktive tags = 16 fine-samples; de 2 andre tag-ene droppes.
        result.SamplesWritten.Should().Be(16);
        harness.FineRepo.Written.Select(s => s.SignalId).Distinct()
            .Should().BeEquivalentTo("DRIVDAL_G1_GEN_P_PV", "DRIVDAL_G1_TURB_VF_PV");

        // Hourly avledet: 8 kvarter = 2 timer × 2 tags = 4 aggregater.
        harness.HourlyRepo.Written.Should().HaveCount(4);
        harness.HourlyRepo.Written.Should().OnlyContain(s => s.TimeUtc.Minute == 0);

        // Logges som "scada" (Endring C) med dropp-telemetri i notes.
        harness.Logger.Entries.Should().ContainSingle();
        var entry = harness.Logger.Entries[0];
        entry.SourceType.Should().Be("scada");
        entry.Notes.Should().Contain("Hourly avledet: 4 aggregater");
        entry.Notes.Should().Contain("16 samples droppet (2 deaktiverte/ukjente tags)");
    }

    [Fact]
    public async Task HourlyImport_FiltrererOgsaa_MenAvlederIkke()
    {
        var harness = new Harness(signalMaps: [Map("DRIVDAL_G1_GEN_P_PV", storeSamples: true)]);

        var csv = BuildCsv(
            ["DRIVDAL_G1_GEN_P_PV", "DRIVDAL_UKJENT_TAG"],
            start: new DateTime(2026, 6, 1, 10, 0, 0),
            stepMinutes: 60,
            rows: 5);

        var result = await harness.Service.ImportMasterCsvAsync(
            "drivdal", "dev-org", csv, CancellationToken.None);

        result.SamplesWritten.Should().Be(5);
        harness.HourlyRepo.Written.Select(s => s.SignalId).Distinct()
            .Should().BeEquivalentTo("DRIVDAL_G1_GEN_P_PV");
        harness.FineRepo.Written.Should().BeEmpty();
        harness.Logger.Entries[0].Notes.Should().Contain("5 samples droppet (1 deaktiverte/ukjente tags)");
    }

    [Fact]
    public async Task AnleggUtenSignalMap_FiltreresIkke()
    {
        // Defensivt: useedet anlegg skal ikke miste all data.
        var harness = new Harness(signalMaps: []);

        var csv = BuildCsv(
            ["DRIVDAL_G1_GEN_P_PV", "DRIVDAL_HVA_SOM_HELST"],
            start: new DateTime(2026, 6, 1, 10, 0, 0),
            stepMinutes: 60,
            rows: 3);

        var result = await harness.Service.ImportMasterCsvAsync(
            "drivdal", "dev-org", csv, CancellationToken.None);

        result.SamplesWritten.Should().Be(6, "ingen whitelist → alt beholdes");
    }

    // ---- Hjelpere -----------------------------------------------------------

    private static SignalMap Map(string signalId, bool storeSamples) => new(
        PlantId: "drivdal",
        SignalId: signalId,
        CsvColumn: $"Cluster1.{signalId}",
        Unit: "kW",
        Role: SignalRole.Other,
        StoreSamples: storeSamples,
        IsActive: storeSamples);

    /// <summary>Bygger en master-CSV (bred, semikolon) som MemoryStream.</summary>
    private static MemoryStream BuildCsv(
        string[] tags, DateTime start, int stepMinutes, int rows)
    {
        var sb = new StringBuilder();
        sb.Append("DateTime");
        foreach (var t in tags)
        {
            sb.Append($";Value (Cluster1.{t});Unit (Cluster1.{t})");
        }
        sb.AppendLine();
        for (var i = 0; i < rows; i++)
        {
            sb.Append((start + TimeSpan.FromMinutes(stepMinutes * i)).ToString("yyyy-MM-dd HH:mm:ss.fff"));
            for (var j = 0; j < tags.Length; j++)
            {
                sb.Append($";{10 * (j + 1)};kW");
            }
            sb.AppendLine();
        }
        return new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private sealed class Harness
    {
        public CapturingSampleRepo HourlyRepo { get; } = new();
        public CapturingFineRepo FineRepo { get; } = new();
        public CapturingImportLogger Logger { get; } = new();
        public ScadaImportService Service { get; }

        public Harness(IReadOnlyList<SignalMap> signalMaps)
        {
            Service = new ScadaImportService(
                HourlyRepo, FineRepo, new ThrowingEventRepo(),
                new StubSignalMapRepo(signalMaps), Logger,
                NullLogger<ScadaImportService>.Instance);
        }
    }

    private sealed class CapturingSampleRepo : IScadaSampleRepository
    {
        public List<ScadaSample> Written { get; } = new();
        public Task<int> BulkInsertAsync(IReadOnlyCollection<ScadaSample> samples, CancellationToken ct)
        {
            Written.AddRange(samples);
            return Task.FromResult(samples.Count);
        }
        public Task<IReadOnlyList<ScadaSample>> ListAsync(string plantId, IReadOnlyCollection<string> signalIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ScadaSample>>(Array.Empty<ScadaSample>());
        public Task<int> DeleteOlderThanAsync(string plantId, DateTimeOffset cutoffUtc, CancellationToken ct) => Task.FromResult(0);
        public Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct) => Task.FromResult(0);
        public Task<int> DeleteAllAsync(CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class CapturingFineRepo : IScadaSampleFineRepository
    {
        public List<ScadaSample> Written { get; } = new();
        public Task<int> BulkInsertAsync(IReadOnlyCollection<ScadaSample> samples, CancellationToken ct)
        {
            Written.AddRange(samples);
            return Task.FromResult(samples.Count);
        }
        public Task<IReadOnlyList<ScadaSample>> ListAsync(string plantId, IReadOnlyCollection<string> signalIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ScadaSample>>(Array.Empty<ScadaSample>());
        public Task<int> DeleteOlderThanAsync(string plantId, DateTimeOffset cutoffUtc, CancellationToken ct) => Task.FromResult(0);
        public Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct) => Task.FromResult(0);
        public Task<int> DeleteAllAsync(CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class StubSignalMapRepo : ISignalMapRepository
    {
        private readonly IReadOnlyList<SignalMap> _maps;
        public StubSignalMapRepo(IReadOnlyList<SignalMap> maps) => _maps = maps;
        public Task<IReadOnlyList<SignalMap>> ListForPlantAsync(string plantId, CancellationToken ct)
            => Task.FromResult(_maps);
        public Task<SignalMap?> GetAsync(string plantId, string signalId, CancellationToken ct)
            => Task.FromResult(_maps.FirstOrDefault(m => m.SignalId == signalId));
        public Task<string?> GetSignalIdForRoleAsync(string plantId, SignalRole role, CancellationToken ct)
            => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<SignalMap>> GetByPlantDamAndRoleAsync(string plantId, string? damId, SignalRole role, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SignalMap>>(Array.Empty<SignalMap>());
        public Task UpsertAsync(SignalMap signalMap, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CapturingImportLogger : IDataImportLogger
    {
        public List<DataImportLogEntry> Entries { get; } = new();
        public Task LogAsync(DataImportLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingEventRepo : IClassifiedEventRepository
    {
        public Task<IReadOnlyList<ClassifiedEvent>> ListAsync(string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<long> CreateAsync(ClassifiedEvent ev, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<bool> CloseAsync(long id, DateTimeOffset endUtc, CancellationToken ct)
            => throw new NotSupportedException();
        public Task UpsertManyAsync(IReadOnlyCollection<ClassifiedEvent> events, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<int> DeleteAllAsync(string ownerOrgId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
