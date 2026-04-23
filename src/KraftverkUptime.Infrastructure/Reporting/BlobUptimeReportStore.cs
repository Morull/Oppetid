using System.Text.Json;
using System.Text.Json.Serialization;
using KraftverkUptime.Core.Storage;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.Storage;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Blob-basert implementasjon av <see cref="IUptimeReportStore"/>. Lagrer
/// <c>UptimeReport</c> som JSON under <c>reports/{ownerOrgId}/{plantId}/{idempotencyKey}.json</c>.
///
/// Samme JSON-representasjon brukes både som persistens-format og som API-respons –
/// det holder formatet stabilt mellom klasifiseringstid og visningstid, og gjør
/// at GET-endepunktet kan strømme bloben direkte til klienten hvis ønskelig.
/// </summary>
public sealed class BlobUptimeReportStore : IUptimeReportStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    private readonly IFileStorage _fileStorage;
    private readonly ILogger<BlobUptimeReportStore> _logger;

    public BlobUptimeReportStore(IFileStorage fileStorage, ILogger<BlobUptimeReportStore> logger)
    {
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Intern – eksponeres for tester og for andre lag som trenger blob-stien.</summary>
    public static string BuildBlobPath(string ownerOrgId, string plantId, string idempotencyKey)
        => $"reports/{ownerOrgId}/{plantId}/{idempotencyKey}.json";

    public async Task SaveAsync(
        string ownerOrgId,
        string plantId,
        string idempotencyKey,
        UptimeReport report,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrgId);
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentNullException.ThrowIfNull(report);

        var path = BuildBlobPath(ownerOrgId, plantId, idempotencyKey);

        using var ms = new MemoryStream();
        await JsonSerializer.SerializeAsync(ms, report, SerializerOptions, ct).ConfigureAwait(false);
        ms.Position = 0;

        await _fileStorage.PutAsync(path, ms, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "UptimeReport persistert for plant {PlantId} (org {OrgId}, key {Key}): {Bytes} bytes",
            plantId, ownerOrgId, idempotencyKey, ms.Length);
    }

    public async Task<UptimeReport?> GetAsync(
        string ownerOrgId,
        string plantId,
        string idempotencyKey,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrgId);
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var path = BuildBlobPath(ownerOrgId, plantId, idempotencyKey);

        if (!await _fileStorage.ExistsAsync(path, ct).ConfigureAwait(false))
        {
            return null;
        }

        await using var stream = await _fileStorage.GetAsync(path, ct).ConfigureAwait(false);
        var report = await JsonSerializer
            .DeserializeAsync<UptimeReport>(stream, SerializerOptions, ct)
            .ConfigureAwait(false);

        return report;
    }
}
