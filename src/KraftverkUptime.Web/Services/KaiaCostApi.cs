using System.Net.Http.Json;
using System.Text.Json;

namespace KraftverkUptime.Web.Services;

/// <summary>
/// Typed HTTP-klient mot KAIA-kostnad-endepunktene
/// (<c>/api/v1/plants/{plantId}/kaia-cost/{idempotencyKey}</c> og
/// <c>/api/v1/portfolio/kaia-cost</c>). Brukes av ReportDetail og Portefolje.
/// </summary>
public sealed class KaiaCostApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;

    public KaiaCostApi(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>KAIA-kostnad for én spesifikk settlement-import.</summary>
    public async Task<KaiaCostDto?> GetForImportAsync(
        string plantId, string idempotencyKey, CancellationToken ct = default)
    {
        var uri = new Uri(
            $"api/v1/plants/{Uri.EscapeDataString(plantId)}/kaia-cost/{Uri.EscapeDataString(idempotencyKey)}",
            UriKind.Relative);
        var resp = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<KaiaCostDto>(JsonOptions, ct).ConfigureAwait(false);
    }

    /// <summary>KAIA-kostnad for alle anlegg i porteføljen for en periode.</summary>
    public async Task<IReadOnlyList<KaiaCostDto>> GetForPortfolioAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var uri = new Uri(
            "api/v1/portfolio/kaia-cost"
            + $"?from={Uri.EscapeDataString(fromUtc.ToString("O"))}"
            + $"&to={Uri.EscapeDataString(toUtc.ToString("O"))}",
            UriKind.Relative);
        var rows = await _http.GetFromJsonAsync<List<KaiaCostDto>>(uri, JsonOptions, ct)
            .ConfigureAwait(false);
        return rows ?? new List<KaiaCostDto>();
    }
}

/// <summary>
/// DTO for KAIA-kostnad — speilet av
/// <c>KraftverkUptime.Modules.Reporting.KaiaCost.KaiaCostResult</c>.
/// Alle beløp er positive kostnader (NOK). Null på Meglerprovisjon/Total =
/// "ukjent" (Summering-fanen manglet); 0 = "ingen handler i perioden".
/// </summary>
public sealed record KaiaCostDto(
    string PlantId,
    string PlantName,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    double? MeglerprovisjonNok,
    double FastAvgiftNok,
    double? TotalNok,
    string? Note);
