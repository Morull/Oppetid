using FluentAssertions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Storage;
using KraftverkUptime.Infrastructure.Reporting;
using KraftverkUptime.Modules.Classification.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

public class BlobUptimeReportStoreTests
{
    [Fact]
    public void BuildBlobPath_Uses_OrgPlantKey_Convention()
    {
        var path = BlobUptimeReportStore.BuildBlobPath("org-1", "drivdal", "abc123");
        path.Should().Be("reports/org-1/drivdal/abc123.json");
    }

    [Fact]
    public async Task SaveAsync_Writes_Json_To_FileStorage_At_Canonical_Path()
    {
        var storage = new InMemoryFileStorage();
        var store = new BlobUptimeReportStore(storage, NullLogger<BlobUptimeReportStore>.Instance);
        var report = SampleReport();

        await store.SaveAsync("org-1", "drivdal", "abc123", report, CancellationToken.None);

        storage.Writes.Should().ContainKey("reports/org-1/drivdal/abc123.json");
        var json = System.Text.Encoding.UTF8.GetString(storage.Writes["reports/org-1/drivdal/abc123.json"]);
        json.Should().Contain("\"plantId\":\"drivdal\"");
        json.Should().Contain("\"periodHours\":0");
    }

    [Fact]
    public async Task GetAsync_Returns_Null_When_Blob_Missing()
    {
        var storage = new InMemoryFileStorage();
        var store = new BlobUptimeReportStore(storage, NullLogger<BlobUptimeReportStore>.Instance);

        var result = await store.GetAsync("org-1", "drivdal", "missing", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_Then_GetAsync_Roundtrips_Report()
    {
        var storage = new InMemoryFileStorage();
        var store = new BlobUptimeReportStore(storage, NullLogger<BlobUptimeReportStore>.Instance);
        var report = SampleReport();

        await store.SaveAsync("org-1", "drivdal", "abc123", report, CancellationToken.None);
        var loaded = await store.GetAsync("org-1", "drivdal", "abc123", CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.PlantId.Should().Be(report.PlantId);
        loaded.PeriodStartUtc.Should().Be(report.PeriodStartUtc);
        loaded.PeriodEndUtc.Should().Be(report.PeriodEndUtc);
    }

    [Fact]
    public async Task SaveAsync_Overwrites_Existing_Blob_On_Same_Key()
    {
        var storage = new InMemoryFileStorage();
        var store = new BlobUptimeReportStore(storage, NullLogger<BlobUptimeReportStore>.Instance);

        await store.SaveAsync("org-1", "drivdal", "abc", SampleReport(hours: 100), CancellationToken.None);
        await store.SaveAsync("org-1", "drivdal", "abc", SampleReport(hours: 200), CancellationToken.None);

        storage.Writes.Should().HaveCount(1);
        var loaded = await store.GetAsync("org-1", "drivdal", "abc", CancellationToken.None);
        loaded!.PeriodHours.Should().Be(200);
    }

    private static UptimeReport SampleReport(int hours = 0) => new()
    {
        PlantId = "drivdal",
        PeriodStartUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        PeriodEndUtc = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        PeriodHours = hours,
        StateCounts = new Dictionary<UnitState, int>(),
        Classified = Array.Empty<ClassifiedHourlyRow>(),
        Kpis = Array.Empty<KpiResult>(),
    };

    private sealed class InMemoryFileStorage : IFileStorage
    {
        public Dictionary<string, byte[]> Writes { get; } = new(StringComparer.Ordinal);

        public async Task<string> PutAsync(string path, Stream content, CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Writes[path] = ms.ToArray();
            return path;
        }

        public Task<Stream> GetAsync(string path, CancellationToken ct = default)
        {
            if (!Writes.TryGetValue(path, out var bytes))
            {
                throw new FileNotFoundException(path);
            }
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }

        public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
            => Task.FromResult(Writes.ContainsKey(path));

        public Task DeleteAsync(string path, CancellationToken ct = default)
        {
            Writes.Remove(path);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var key in Writes.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    yield return key;
                }
            }
            await Task.CompletedTask;
        }
    }
}
