using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KraftverkUptime.Web.Services;

/// <summary>
/// Typed HTTP-klient mot API-endepunktene introdusert i Steg 4/5.
/// Wrapp-er <see cref="HttpClient"/> med DTO-materialisering og fornuftige
/// feil-kast. Brukes av Razor-sider i Web-prosjektet.
/// </summary>
public sealed class ReportsApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;

    public ReportsApi(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<IReadOnlyList<PlantDto>> ListPlantsAsync(CancellationToken ct = default)
    {
        var env = await _http
            .GetFromJsonAsync<PagedEnvelope<PlantDto>>("api/v1/plants", JsonOptions, ct)
            .ConfigureAwait(false);
        return env?.Items ?? (IReadOnlyList<PlantDto>)Array.Empty<PlantDto>();
    }

    public async Task<IReadOnlyList<SettlementImportSummary>> ListSettlementsAsync(
        string plantId,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var url = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/settlements?limit={limit}";
        if (fromUtc.HasValue && toUtc.HasValue)
        {
            url += $"&from={Uri.EscapeDataString(fromUtc.Value.UtcDateTime.ToString("o"))}";
            url += $"&to={Uri.EscapeDataString(toUtc.Value.UtcDateTime.ToString("o"))}";
        }

        var items = await _http
            .GetFromJsonAsync<List<SettlementImportSummary>>(url, JsonOptions, ct)
            .ConfigureAwait(false);
        return items ?? (IReadOnlyList<SettlementImportSummary>)Array.Empty<SettlementImportSummary>();
    }

    public async Task<UptimeReportSummary?> GetReportAsync(
        string plantId, string idempotencyKey, CancellationToken ct = default)
    {
        var uri = new Uri(
            $"api/v1/plants/{Uri.EscapeDataString(plantId)}/settlements/{Uri.EscapeDataString(idempotencyKey)}/report",
            UriKind.Relative);
        var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<UptimeReportSummary>(JsonOptions, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Bygger absolutt URL til XLSX-nedlasting for bruk i
    /// <c>&lt;a href&gt;</c>. Nettleseren håndterer selve nedlastingen.
    /// </summary>
    public Uri BuildXlsxDownloadUri(string plantId, string idempotencyKey)
    {
        var baseAddress = _http.BaseAddress ?? throw new InvalidOperationException("HttpClient mangler BaseAddress.");
        var relative = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/settlements/{Uri.EscapeDataString(idempotencyKey)}/report/xlsx";
        return new Uri(baseAddress, relative);
    }

    public async Task<SettlementUploadResponse> UploadSettlementAsync(
        string plantId,
        Stream fileStream,
        string fileName,
        string contentType,
        CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(fileContent, "file", fileName);

        var uri = new Uri(
            $"api/v1/plants/{Uri.EscapeDataString(plantId)}/settlements",
            UriKind.Relative);
        var response = await _http.PostAsync(uri, form, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var body = await response.Content
            .ReadFromJsonAsync<SettlementUploadResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return body ?? throw new InvalidOperationException("Tom respons fra upload-endepunkt.");
    }
}

// --------- DTO-er -----------------------------------------------------------

public sealed record PlantDto(
    string Id, string Name, string Type, double InstalledCapacityMw, string TimeZone);

public sealed record PagedEnvelope<T>(
    IReadOnlyList<T> Items, string? NextCursor, int PageSize);

public sealed record SettlementImportSummary(
    string PlantId,
    string IdempotencyKey,
    string PlantName,
    string SchemaVersion,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    int HourCount,
    int IssueCount,
    DateTimeOffset ImportedAtUtc,
    bool ReportAvailable);

public sealed record SettlementUploadResponse(
    string PlantId,
    string OwnerOrgId,
    string BlobPath,
    string IdempotencyKey,
    string? CorrelationId);

/// <summary>
/// Slank visnings-variant av <c>UptimeReport</c>. Speiler feltene Web trenger
/// uten å dra inn Modules.Classification-prosjektet som referanse.
/// </summary>
public sealed record UptimeReportSummary(
    string PlantId,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    int PeriodHours,
    IReadOnlyDictionary<string, int> StateCounts,
    IReadOnlyList<KpiDto> Kpis,
    IReadOnlyList<ClassifiedHourDto> Classified);

public sealed record KpiDto(
    string Name,
    double? Value,
    string Unit,
    int HoursBasis,
    double Confidence,
    string Category,
    string Definition);

public sealed record ClassifiedHourDto(
    DateTimeOffset TimeUtc,
    string State,
    double Confidence,
    string CauseCode,
    double? MwhElhub,
    double? ProduksjonplanMwh,
    double? SpotbudMwh,
    double? SpotprisNokMwh,
    string Rationale);
