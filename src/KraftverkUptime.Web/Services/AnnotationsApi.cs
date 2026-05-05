using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KraftverkUptime.Web.Services;

/// <summary>
/// Typed klient for annoterings-endepunktene.
///
/// Auth-modell i v1: anonym (samme som ReportsApi). Når MSAL er på plass
/// vil HttpClient automatisk få Bearer-token via DelegatingHandler — denne
/// klassen trenger ingen endringer.
///
/// Overlapp-håndtering: <see cref="CreateAsync"/> og <see cref="UpdateAsync"/>
/// returnerer en discriminated union via <see cref="AnnotationSaveResult"/>.
/// Klienten kan vise "erstatt disse?"-dialog og gjenta kallet med
/// <c>replaceIds</c> satt.
/// </summary>
public sealed class AnnotationsApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;

    public AnnotationsApi(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<IReadOnlyList<AnnotationCategoryDto>> ListCategoriesAsync(CancellationToken ct = default)
    {
        var items = await _http
            .GetFromJsonAsync<List<AnnotationCategoryDto>>("api/v1/annotations/categories", JsonOptions, ct)
            .ConfigureAwait(false);
        return items ?? (IReadOnlyList<AnnotationCategoryDto>)Array.Empty<AnnotationCategoryDto>();
    }

    /// <summary>Henter alle kategorier inkl. inaktive — for admin-skjermen.</summary>
    public async Task<IReadOnlyList<AnnotationCategoryDto>> ListAllCategoriesAsync(CancellationToken ct = default)
    {
        var items = await _http
            .GetFromJsonAsync<List<AnnotationCategoryDto>>("api/v1/annotations/categories/all", JsonOptions, ct)
            .ConfigureAwait(false);
        return items ?? (IReadOnlyList<AnnotationCategoryDto>)Array.Empty<AnnotationCategoryDto>();
    }

    public async Task<AnnotationCategoryDto> CreateCategoryAsync(
        string id, string displayName, string colorHex,
        string unitStateOverride, int sortOrder, bool isActive,
        string? description,
        CancellationToken ct = default)
    {
        var body = new { id, displayName, colorHex, unitStateOverride, sortOrder, isActive, description };
        var resp = await _http.PostAsJsonAsync("api/v1/annotations/categories", body, JsonOptions, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AnnotationCategoryDto>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra create-category.");
    }

    public async Task<AnnotationCategoryDto> UpdateCategoryAsync(
        string id, string? displayName, string? colorHex,
        string? unitStateOverride, int? sortOrder, bool? isActive,
        string? description,
        CancellationToken ct = default)
    {
        var body = new { displayName, colorHex, unitStateOverride, sortOrder, isActive, description };
        var url = $"api/v1/annotations/categories/{Uri.EscapeDataString(id)}";
        var resp = await _http.PutAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<AnnotationCategoryDto>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra update-category.");
    }

    public async Task DeleteCategoryAsync(string id, CancellationToken ct = default)
    {
        var url = new Uri($"api/v1/annotations/categories/{Uri.EscapeDataString(id)}", UriKind.Relative);
        var resp = await _http.DeleteAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.BadRequest)
        {
            // Server svarer 400 ved system-kategori eller referert kategori.
            // Les ut detail-feltet til en exception som UI kan vise.
            var problem = await resp.Content
                .ReadFromJsonAsync<DeleteCategoryProblem>(JsonOptions, ct)
                .ConfigureAwait(false);
            throw new InvalidOperationException(problem?.Detail ?? "Kunne ikke slette kategori.");
        }
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// ProblemDetails-payload fra backend ved 400 BadRequest. Public slik at
    /// JSON-deserialisering kan reflektere over typen — CA1812 trigget på
    /// internal record som "aldri instansiert" siden EF deserialisering
    /// regnes som ikke-instansiering av analysatoren.
    /// </summary>
    public sealed record DeleteCategoryProblem(string? Title, string? Detail);

    public async Task<IReadOnlyList<AnnotationDto>> ListAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url =
            $"api/v1/plants/{Uri.EscapeDataString(plantId)}/annotations" +
            $"?from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}" +
            $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";

        var items = await _http
            .GetFromJsonAsync<List<AnnotationDto>>(url, JsonOptions, ct)
            .ConfigureAwait(false);
        return items ?? (IReadOnlyList<AnnotationDto>)Array.Empty<AnnotationDto>();
    }

    public async Task<AnnotationSaveResult> CreateAsync(
        string plantId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string categoryId,
        string? comment,
        IReadOnlyList<long>? replaceIds,
        CancellationToken ct = default)
    {
        var body = new
        {
            startUtc,
            endUtc,
            categoryId,
            comment,
            replaceIds
        };
        var url = new Uri($"api/v1/plants/{Uri.EscapeDataString(plantId)}/annotations", UriKind.Relative);
        var response = await _http.PostAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        return await ParseSaveResultAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<AnnotationSaveResult> UpdateAsync(
        string plantId,
        long id,
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        string? categoryId,
        string? comment,
        IReadOnlyList<long>? replaceIds,
        CancellationToken ct = default)
    {
        var body = new
        {
            startUtc,
            endUtc,
            categoryId,
            comment,
            replaceIds
        };
        var url = new Uri($"api/v1/plants/{Uri.EscapeDataString(plantId)}/annotations/{id}", UriKind.Relative);
        var response = await _http.PatchAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        return await ParseSaveResultAsync(response, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string plantId, long id, CancellationToken ct = default)
    {
        var url = new Uri($"api/v1/plants/{Uri.EscapeDataString(plantId)}/annotations/{id}", UriKind.Relative);
        var response = await _http.DeleteAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return; // idempotent — slettet er slettet
        }
        response.EnsureSuccessStatusCode();
    }

    // ---- Cause aliases (admin) ---------------------------------------------

    /// <summary>Lister alle cause-aliaser sortert alfabetisk på cause-kode.</summary>
    public async Task<IReadOnlyList<CauseAliasDto>> ListCauseAliasesAsync(CancellationToken ct = default)
    {
        var items = await _http
            .GetFromJsonAsync<List<CauseAliasDto>>("api/v1/admin/cause-aliases", JsonOptions, ct)
            .ConfigureAwait(false);
        return items ?? (IReadOnlyList<CauseAliasDto>)Array.Empty<CauseAliasDto>();
    }

    /// <summary>Oppretter eller oppdaterer alias for en cause-kode.</summary>
    public async Task<CauseAliasDto> UpsertCauseAliasAsync(
        string causeCode, string displayText, CancellationToken ct = default)
    {
        var url = $"api/v1/admin/cause-aliases/{Uri.EscapeDataString(causeCode)}";
        var resp = await _http.PutAsJsonAsync(url, new { displayText }, JsonOptions, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<CauseAliasDto>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra upsert-cause-alias.");
    }

    /// <summary>Sletter alias for en cause-kode (UI faller tilbake til intern kode).</summary>
    public async Task DeleteCauseAliasAsync(string causeCode, CancellationToken ct = default)
    {
        var url = new Uri($"api/v1/admin/cause-aliases/{Uri.EscapeDataString(causeCode)}", UriKind.Relative);
        var resp = await _http.DeleteAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<AnnotationSaveResult> ParseSaveResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await response.Content
                .ReadFromJsonAsync<AnnotationOverlapConflictDto>(JsonOptions, ct)
                .ConfigureAwait(false);
            return new AnnotationSaveResult.Conflict(
                conflict?.Message ?? "Tidsrommet overlapper eksisterende annoteringer.",
                conflict?.Overlapping ?? Array.Empty<AnnotationDto>());
        }

        response.EnsureSuccessStatusCode();
        var saved = await response.Content
            .ReadFromJsonAsync<AnnotationDto>(JsonOptions, ct)
            .ConfigureAwait(false);
        if (saved is null)
        {
            throw new InvalidOperationException("Tom respons fra annoterings-endepunkt.");
        }
        return new AnnotationSaveResult.Saved(saved);
    }
}

// --------- DTO-er -----------------------------------------------------------

public sealed record AnnotationDto(
    long Id,
    string PlantId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CategoryId,
    string? Comment,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy);

public sealed record AnnotationCategoryDto(
    string Id,
    string DisplayName,
    string ColorHex,
    string UnitStateOverride,
    int SortOrder,
    bool IsActive,
    bool IsSystem,
    string? Description = null);

public sealed record AnnotationOverlapConflictDto(
    string Message,
    IReadOnlyList<AnnotationDto> Overlapping);

public sealed record CauseAliasDto(string CauseCode, string DisplayText, DateTimeOffset UpdatedAt);

/// <summary>Diskriminert union for resultat av POST/PATCH.</summary>
public abstract record AnnotationSaveResult
{
    public sealed record Saved(AnnotationDto Annotation) : AnnotationSaveResult;
    public sealed record Conflict(string Message, IReadOnlyList<AnnotationDto> Overlapping) : AnnotationSaveResult;
}
