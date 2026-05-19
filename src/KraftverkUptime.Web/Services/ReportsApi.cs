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

    public async Task<PlantDto?> GetPlantAsync(string plantId, CancellationToken ct = default)
    {
        var uri = new Uri($"api/v1/plants/{Uri.EscapeDataString(plantId)}", UriKind.Relative);
        var resp = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<PlantDto>(JsonOptions, ct).ConfigureAwait(false);
    }

    public async Task<PlantDto> UpdatePlantAsync(string plantId, UpdatePlantRequest body, CancellationToken ct = default)
    {
        var uri = new Uri($"api/v1/plants/{Uri.EscapeDataString(plantId)}", UriKind.Relative);
        var resp = await _http.PutAsJsonAsync(uri, body, JsonOptions, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<PlantDto>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra plant-update.");
    }

    /// <summary>Sletter en spesifikk settlement-import + tilhørende rapport-blob.</summary>
    public async Task DeleteSettlementAsync(string plantId, string idempotencyKey, CancellationToken ct = default)
    {
        var uri = new Uri(
            $"api/v1/plants/{Uri.EscapeDataString(plantId)}/settlements/{Uri.EscapeDataString(idempotencyKey)}",
            UriKind.Relative);
        var resp = await _http.DeleteAsync(uri, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Sletter ALL importert data for et anlegg (settlement, classified events,
    /// SCADA samples, annotations, report-blobs). Plant-raden beholdes.
    /// confirmText må være lik plantId for å bekrefte.
    /// </summary>
    public async Task<ResetResultDto> ResetPlantDataAsync(
        string plantId, string confirmText, CancellationToken ct = default)
    {
        var uri = new Uri(
            $"api/v1/plants/{Uri.EscapeDataString(plantId)}/data",
            UriKind.Relative);
        var body = new ResetPlantDataRequestDto(confirmText);
        var json = System.Text.Json.JsonSerializer.Serialize(body, JsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Delete, uri)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ResetResultDto>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra plant data-reset.");
    }

    /// <summary>Henter alle dammer for et anlegg, sortert på cascade_position.</summary>
    public async Task<IReadOnlyList<DamDto>> ListDamsAsync(
        string plantId, CancellationToken ct = default)
    {
        var uri = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/dams";
        var resp = await _http.GetFromJsonAsync<List<DamDto>>(uri, JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? new List<DamDto>();
    }

    /// <summary>Oppdaterer HRV/LRV/Volum/IsTurbineIntake for en eksisterende dam.</summary>
    public async Task<DamDto> UpdateDamAsync(
        string plantId, string damId, UpdateDamRequestDto body, CancellationToken ct = default)
    {
        var uri = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/dams/{Uri.EscapeDataString(damId)}";
        var resp = await _http.PutAsJsonAsync(uri, body, JsonOptions, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<DamDto>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra dam-update.");
    }

    /// <summary>Oppretter en ny dam (typisk for kaskade-utvidelse).</summary>
    public async Task<DamDto> CreateDamAsync(
        string plantId, CreateDamRequestDto body, CancellationToken ct = default)
    {
        var uri = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/dams";
        var resp = await _http.PostAsJsonAsync(uri, body, JsonOptions, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<DamDto>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra dam-create.");
    }

    /// <summary>Sletter en dam. Krever at minst én annen dam (terminal) er igjen.</summary>
    public async Task DeleteDamAsync(string plantId, string damId, CancellationToken ct = default)
    {
        var uri = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/dams/{Uri.EscapeDataString(damId)}";
        var resp = await _http.DeleteAsync(new Uri(uri, UriKind.Relative), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Lister alle signal-mappinger for et anlegg, evt. filtrert på role/damId.</summary>
    public async Task<IReadOnlyList<SignalMapDto>> ListSignalMapsAsync(
        string plantId, string? role = null, string? damId = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(role)) query.Add($"role={Uri.EscapeDataString(role)}");
        if (!string.IsNullOrEmpty(damId)) query.Add($"damId={Uri.EscapeDataString(damId)}");
        var qs = query.Count > 0 ? "?" + string.Join("&", query) : "";
        var uri = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/signal-maps{qs}";
        var resp = await _http.GetFromJsonAsync<List<SignalMapDto>>(uri, JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? new List<SignalMapDto>();
    }

    /// <summary>Opprett eller oppdater en SCADA-tag-mapping.</summary>
    public async Task<SignalMapDto> UpsertSignalMapAsync(
        string plantId, UpsertSignalMapRequestDto body, CancellationToken ct = default)
    {
        var uri = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/signal-maps";
        var resp = await _http.PostAsJsonAsync(uri, body, JsonOptions, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SignalMapDto>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra signal-map-upsert.");
    }

    /// <summary>Slett en signal-mapping.</summary>
    public async Task DeleteSignalMapAsync(string plantId, string signalId, CancellationToken ct = default)
    {
        var uri = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/signal-maps/{Uri.EscapeDataString(signalId)}";
        var resp = await _http.DeleteAsync(new Uri(uri, UriKind.Relative), ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Lister tilgjengelige SCADA-tags (signal_id-er) som er observert i samples.</summary>
    public async Task<IReadOnlyList<ScadaTagDto>> ListScadaTagsAsync(
        string plantId, CancellationToken ct = default)
    {
        var uri = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/scada-tags";
        var resp = await _http.GetFromJsonAsync<List<ScadaTagDto>>(uri, JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? new List<ScadaTagDto>();
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

    /// <summary>
    /// Laster opp én operlog-CSV med events fra flere stasjoner.
    /// Speiler <c>POST /api/v1/operlog/multi-plant</c>. Splitter på station-feltet
    /// og ruter hver event til riktig anlegg.
    /// </summary>
    public async Task<MultiPlantOperlogResponse> UploadMultiPlantOperlogAsync(
        Stream fileStream, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/csv");
        form.Add(fileContent, "file", fileName);

        var resp = await _http.PostAsync(
            new Uri("api/v1/operlog/multi-plant", UriKind.Relative), form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        return await resp.Content
            .ReadFromJsonAsync<MultiPlantOperlogResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra multi-plant operlog upload.");
    }

    /// <summary>
    /// Laster opp én SCADA master-CSV med tags fra flere anlegg.
    /// Speiler <c>POST /api/v1/scada/multi-plant</c>. Splitter per signal-prefiks
    /// og oppretter én data_imports-rad per anlegg.
    /// </summary>
    public async Task<MultiPlantScadaResponse> UploadMultiPlantScadaMasterAsync(
        Stream fileStream, string fileName, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/csv");
        form.Add(fileContent, "file", fileName);

        var resp = await _http.PostAsync(
            new Uri("api/v1/scada/multi-plant", UriKind.Relative), form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        return await resp.Content
            .ReadFromJsonAsync<MultiPlantScadaResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra multi-plant SCADA upload.");
    }

    /// <summary>
    /// Laster opp en multi-anleggs-eksport (én Excel med flere plant-faner).
    /// Speiler <c>POST /api/v1/settlements/multi-plant</c>. Auto-oppretter
    /// nye plants ved behov.
    /// </summary>
    public async Task<MultiPlantImportResponse> UploadMultiPlantSettlementAsync(
        Stream fileStream, string fileName, string contentType, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(fileContent, "file", fileName);

        var resp = await _http.PostAsync(
            new Uri("api/v1/settlements/multi-plant", UriKind.Relative), form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        return await resp.Content
            .ReadFromJsonAsync<MultiPlantImportResponse>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra multi-plant upload.");
    }

    /// <summary>
    /// Laster opp SCADA master-CSV (tidsserier). Speiler API-endepunktet
    /// <c>POST /api/v1/plants/{plantId}/scada</c>.
    /// </summary>
    public async Task<ScadaImportResultDto> UploadScadaMasterAsync(
        string plantId, Stream fileStream, string fileName, CancellationToken ct = default)
    {
        var resp = await PostScadaCsvAsync(
            $"api/v1/plants/{Uri.EscapeDataString(plantId)}/scada",
            fileStream, fileName, ct).ConfigureAwait(false);
        return await resp.Content
            .ReadFromJsonAsync<ScadaImportResultDto>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra SCADA master-upload.");
    }

    /// <summary>
    /// Laster opp SCADA operlog-CSV (events). Speiler API-endepunktet
    /// <c>POST /api/v1/plants/{plantId}/scada/operlog</c>.
    /// </summary>
    public async Task<OperlogImportResultDto> UploadScadaOperlogAsync(
        string plantId, Stream fileStream, string fileName, CancellationToken ct = default)
    {
        var resp = await PostScadaCsvAsync(
            $"api/v1/plants/{Uri.EscapeDataString(plantId)}/scada/operlog",
            fileStream, fileName, ct).ConfigureAwait(false);
        return await resp.Content
            .ReadFromJsonAsync<OperlogImportResultDto>(JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra SCADA operlog-upload.");
    }

    private async Task<HttpResponseMessage> PostScadaCsvAsync(
        string relativeUrl, Stream fileStream, string fileName, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("text/csv");
        form.Add(fileContent, "file", fileName);

        var response = await _http.PostAsync(new Uri(relativeUrl, UriKind.Relative), form, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }
}

// --------- DTO-er -----------------------------------------------------------

/// <summary>
/// Plant-grunndata. <see cref="DeratingThreshold"/> er per-anlegg terskel for
/// plan-avvik som teller som ForcedDerating; default 0.80 (= 20 % toleranse).
/// Null fra ListAsync (paginert liste viser kun grunn-felter); satt fra GET.
/// </summary>
public sealed record PlantDto(
    string Id, string Name, string Type, double InstalledCapacityMw, string TimeZone,
    double? DeratingThreshold = null);

/// <summary>
/// Body for <c>PUT /api/v1/plants/{plantId}</c>. <see cref="Type"/> er enum-string —
/// "Regulated", "RunOfRiver", "Mixed" eller "Pumped".
/// <see cref="DeratingThreshold"/> i (0, 1]; null beholder eksisterende verdi.
/// </summary>
public sealed record UpdatePlantRequest(
    string Name,
    string Type,
    double InstalledCapacityMw,
    string TimeZone,
    double? DeratingThreshold = null);

/// <summary>Bekreftelses-body for <c>DELETE /api/v1/plants/{plantId}/data</c>.</summary>
public sealed record ResetPlantDataRequestDto(string ConfirmText);

public sealed record ResetResultDto(
    string PlantId,
    int ReportsDeleted,
    int ImportsDeleted,
    int EventsDeleted,
    int SamplesDeleted,
    int AnnotationsDeleted);

public sealed record DamDto(
    string PlantId,
    string DamId,
    string Name,
    int CascadePosition,
    bool IsTurbineIntake,
    double? HrvMoh,
    double? LrvMoh,
    double? VolumeMm3,
    int? OverflowProxyThresholdCm = null);

public sealed record UpdateDamRequestDto(
    string? Name,
    int? CascadePosition,
    bool IsTurbineIntake,
    double? HrvMoh,
    double? LrvMoh,
    double? VolumeMm3,
    int? OverflowProxyThresholdCm = null);

public sealed record CreateDamRequestDto(
    string DamId,
    string Name,
    int? CascadePosition,
    bool IsTurbineIntake,
    double? HrvMoh,
    double? LrvMoh,
    double? VolumeMm3,
    int? OverflowProxyThresholdCm = null);

public sealed record SignalMapDto(
    string PlantId,
    string SignalId,
    string CsvColumn,
    string Unit,
    string Role,
    bool StoreSamples,
    bool IsActive,
    string? DamId);

public sealed record UpsertSignalMapRequestDto(
    string SignalId,
    string CsvColumn,
    string? Unit,
    string Role,           // SignalRole-enum-navn (sendes som streng)
    string? DamId,
    bool? StoreSamples,
    bool? IsActive);

public sealed record ScadaTagDto(
    string SignalId,
    bool IsMapped,
    string? Role,
    string? DamId);

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

public sealed record MultiPlantOperlogResponse(
    int TotalRowsParsed,
    int TotalRowsSkipped,
    int UnknownStations,
    IReadOnlyList<string> UnknownStationNames,
    IReadOnlyList<PlantOperlogResultDto> PerPlant);

public sealed record PlantOperlogResultDto(
    string PlantId,
    int EventsImported);

public sealed record MultiPlantScadaResponse(
    int TotalRowsParsed,
    int TotalRowsSkipped,
    IReadOnlyList<string> UnknownSignals,
    IReadOnlyList<PlantScadaResultDto> PerPlant);

public sealed record PlantScadaResultDto(
    string PlantId,
    int SignalCount,
    int SamplesWritten);

public sealed record MultiPlantImportResultDto(
    string PlantId,
    string PlantName,
    string IdempotencyKey,
    int HourCount,
    int IssueCount,
    bool PlantCreated);

public sealed record MultiPlantImportResponse(
    string BlobPath,
    int ImportCount,
    int SkippedCount,
    IReadOnlyList<MultiPlantImportResultDto> Imports);

public sealed record ScadaImportResultDto(
    string PlantId,
    int SignalCount,
    int RowsParsed,
    int RowsSkipped,
    int SamplesWritten);

public sealed record OperlogImportResultDto(
    string PlantId,
    int RowsParsed,
    int RowsSkipped,
    int EventsWritten);

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
