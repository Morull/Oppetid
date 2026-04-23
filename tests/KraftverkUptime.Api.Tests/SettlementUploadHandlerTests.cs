using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using KraftverkUptime.Api.Endpoints;
using KraftverkUptime.Api.Options;
using KraftverkUptime.Core.Jobs;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Core.Storage;
using KraftverkUptime.Modules.Settlement.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KraftverkUptime.Api.Tests;

public class SettlementUploadHandlerTests
{
    [Fact]
    public async Task HandleAsync_Uses_Dev_Default_When_CurrentUser_Is_System()
    {
        var (storage, queue, handler, _) = BuildHandler();

        await handler.HandleAsync(
            plantId: "drivdal",
            fileContent: BuildStream("hello"),
            fileName: "Avregning.xlsx",
            correlationId: "trace-1",
            ct: CancellationToken.None);

        storage.LastPath.Should().StartWith("settlements/dev-org/drivdal/");
        queue.Last.Should().NotBeNull();
        queue.Last!.OwnerOrgId.Should().Be("dev-org");
        queue.Last.PlantId.Should().Be("drivdal");
    }

    [Fact]
    public async Task HandleAsync_Uses_CurrentUser_OrgId_When_Not_System()
    {
        var (storage, queue, handler, _) = BuildHandler(currentUser: new FakeCurrentUser("org-42"));

        await handler.HandleAsync(
            plantId: "plant-1",
            fileContent: BuildStream("payload"),
            fileName: null,
            correlationId: null,
            ct: CancellationToken.None);

        storage.LastPath.Should().StartWith("settlements/org-42/plant-1/");
        queue.Last!.OwnerOrgId.Should().Be("org-42");
    }

    [Fact]
    public async Task HandleAsync_Idempotency_Is_Lowercase_Hex_SHA256_Of_Content()
    {
        const string content = "excel-bytes-her";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

        var (_, queue, handler, _) = BuildHandler();

        var result = await handler.HandleAsync(
            plantId: "plant-1",
            fileContent: BuildStream(content),
            fileName: "a.xlsx",
            correlationId: null,
            ct: CancellationToken.None);

        result.IdempotencyKey.Should().Be(expected);
        queue.Last!.IdempotencyKey.Should().Be(expected);
    }

    [Fact]
    public async Task HandleAsync_Same_Content_Twice_Produces_Same_IdempotencyKey()
    {
        var (_, _, handler, _) = BuildHandler();

        var first = await handler.HandleAsync(
            "plant-1", BuildStream("identical"), "a.xlsx", null, CancellationToken.None);

        var second = await handler.HandleAsync(
            "plant-1", BuildStream("identical"), "b.xlsx", null, CancellationToken.None);

        second.IdempotencyKey.Should().Be(first.IdempotencyKey);
    }

    [Fact]
    public async Task HandleAsync_Writes_File_Contents_To_Storage()
    {
        const string content = "stream-me-through";
        var (storage, _, handler, _) = BuildHandler();

        await handler.HandleAsync(
            "plant-1", BuildStream(content), "x.xlsx", null, CancellationToken.None);

        storage.LastBytes.Should().NotBeNull();
        Encoding.UTF8.GetString(storage.LastBytes!).Should().Be(content);
    }

    [Fact]
    public async Task HandleAsync_Builds_Blob_Path_With_Sanitized_Filename_And_Utc_Timestamp()
    {
        var fixedNow = new DateTimeOffset(2026, 4, 23, 12, 34, 56, 789, TimeSpan.Zero);
        var (storage, _, handler, _) = BuildHandler(clock: new FakeTimeProvider(fixedNow));

        await handler.HandleAsync(
            "drivdal",
            BuildStream("x"),
            fileName: "Avregning Drivdal Q1 2026.XLSX",
            correlationId: null,
            ct: CancellationToken.None);

        var expectedPrefix = "settlements/dev-org/drivdal/"
            + fixedNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
            + "-";

        storage.LastPath.Should().StartWith(expectedPrefix);
        storage.LastPath.Should().EndWith("avregning_drivdal_q1_2026.xlsx");
    }

    [Fact]
    public async Task HandleAsync_Falls_Back_To_Default_Filename_When_Missing()
    {
        var (storage, _, handler, _) = BuildHandler();

        await handler.HandleAsync(
            "plant-1", BuildStream("x"), fileName: null, correlationId: null, ct: CancellationToken.None);

        storage.LastPath.Should().EndWith("-settlement.xlsx");
    }

    [Fact]
    public async Task HandleAsync_Throws_When_System_User_And_No_DevDefault_Set()
    {
        var opts = new SettlementUploadOptions { DevDefaultOwnerOrgId = null };
        var (_, _, handler, _) = BuildHandler(options: opts);

        var act = async () => await handler.HandleAsync(
            "plant-1", BuildStream("x"), "a.xlsx", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DevDefaultOwnerOrgId*");
    }

    [Fact]
    public async Task HandleAsync_Enqueues_ParseSettlementJob_With_Full_Payload()
    {
        var (_, queue, handler, _) = BuildHandler();

        var result = await handler.HandleAsync(
            "plant-1",
            BuildStream("payload"),
            "a.xlsx",
            correlationId: "req-123",
            ct: CancellationToken.None);

        queue.Enqueued.Should().HaveCount(1);
        queue.Last!.PlantId.Should().Be("plant-1");
        queue.Last.OwnerOrgId.Should().Be("dev-org");
        queue.Last.BlobPath.Should().Be(result.BlobPath);
        queue.Last.IdempotencyKey.Should().Be(result.IdempotencyKey);
        queue.Last.CorrelationId.Should().Be("req-123");
    }

    [Theory]
    [InlineData("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", true)]
    [InlineData("application/vnd.ms-excel", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData("APPLICATION/VND.MS-EXCEL; charset=utf-8", true)]
    [InlineData("text/plain", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsContentTypeAllowed_Matches_Whitelist(string? contentType, bool expected)
    {
        var (_, _, handler, _) = BuildHandler();
        handler.IsContentTypeAllowed(contentType).Should().Be(expected);
    }

    // --- Helpers -------------------------------------------------------------

    private static Stream BuildStream(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    private static (FakeFileStorage Storage, FakeJobQueue Queue, SettlementUploadHandler Handler, SettlementUploadOptions Options) BuildHandler(
        SettlementUploadOptions? options = null,
        ICurrentUser? currentUser = null,
        TimeProvider? clock = null)
    {
        var opts = options ?? new SettlementUploadOptions { DevDefaultOwnerOrgId = "dev-org" };
        var storage = new FakeFileStorage();
        var queue = new FakeJobQueue();

        var handler = new SettlementUploadHandler(
            storage,
            queue,
            currentUser ?? new FakeCurrentUser("system"),
            Microsoft.Extensions.Options.Options.Create(opts),
            clock ?? TimeProvider.System,
            NullLogger<SettlementUploadHandler>.Instance);

        return (storage, queue, handler, opts);
    }

    private sealed class FakeFileStorage : IFileStorage
    {
        public string? LastPath { get; private set; }
        public byte[]? LastBytes { get; private set; }

        public async Task<string> PutAsync(string path, Stream content, CancellationToken ct = default)
        {
            LastPath = path;
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct).ConfigureAwait(false);
            LastBytes = ms.ToArray();
            return path;
        }

        public Task<Stream> GetAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(string path, CancellationToken ct = default)
            => throw new NotSupportedException();
        public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeJobQueue : IJobQueue
    {
        public List<ParseSettlementJob> Enqueued { get; } = new();
        public ParseSettlementJob? Last => Enqueued.Count == 0 ? null : Enqueued[^1];

        public Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : notnull
        {
            if (job is ParseSettlementJob ps)
            {
                Enqueued.Add(ps);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public FakeCurrentUser(string orgId)
        {
            OrgId = orgId;
            UserId = orgId == "system" ? "system" : "user-1";
            Roles = new HashSet<string>();
            AccessiblePlantIds = new HashSet<string>();
            HasAllPlantsAccess = false;
        }

        public string UserId { get; }
        public string OrgId { get; }
        public IReadOnlySet<string> Roles { get; }
        public bool HasAllPlantsAccess { get; }
        public IReadOnlySet<string> AccessiblePlantIds { get; }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) { _now = now; }
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
