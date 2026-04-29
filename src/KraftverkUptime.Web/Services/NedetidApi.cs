using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KraftverkUptime.Web.Services;

/// <summary>
/// Typed klient mot /api/v1/plants/{plantId}/nedetid og /vakt-roi.
/// Holder DTO-ene fri-frikoblet fra API-prosjektet — Web-prosjektet skal
/// bygges som WASM uten serverside-referanser.
/// </summary>
public sealed class NedetidApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly HttpClient _http;

    public NedetidApi(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<NedetidResponse> GetNedetidAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "nedetid", fromUtc, toUtc, format: null, kapasitetsfaktor: null);
        var resp = await _http.GetFromJsonAsync<NedetidResponse>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /nedetid.");
    }

    public async Task<VaktRoiResponse> GetVaktRoiAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        double? kapasitetsfaktor = null, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "vakt-roi", fromUtc, toUtc, format: null, kapasitetsfaktor);
        var resp = await _http.GetFromJsonAsync<VaktRoiResponse>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /vakt-roi.");
    }

    public async Task<EffektivitetResponse> GetEffektivitetAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "effektivitet", fromUtc, toUtc, format: null, kapasitetsfaktor: null);
        var resp = await _http.GetFromJsonAsync<EffektivitetResponse>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /effektivitet.");
    }

    /// <summary>Bygger nedlastings-URL for CSV-eksport (åpnes direkte i ny fane).</summary>
    public Uri BuildCsvUri(string plantId, string endpoint, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var baseAddress = _http.BaseAddress ?? throw new InvalidOperationException("HttpClient mangler BaseAddress.");
        var rel = BuildUrl(plantId, endpoint, fromUtc, toUtc, format: "csv", kapasitetsfaktor: null);
        return new Uri(baseAddress, rel);
    }

    private static string BuildUrl(string plantId, string endpoint,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, string? format, double? kapasitetsfaktor)
    {
        var qs = $"from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}"
               + $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        if (!string.IsNullOrEmpty(format))
        {
            qs += $"&format={Uri.EscapeDataString(format)}";
        }
        if (kapasitetsfaktor.HasValue)
        {
            qs += $"&kapasitetsfaktor={kapasitetsfaktor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }
        return $"api/v1/plants/{Uri.EscapeDataString(plantId)}/{endpoint}?{qs}";
    }
}

// --- DTO-er (speiler API-kontrakten, men holdes i Web-prosjektet for å unngå
//     å dra inn KraftverkUptime.Api-prosjektet i WASM-bygget) -----------------

public sealed record NedetidEventDto(
    string PlantId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double VarighetTimer,
    string State,
    string Kategori,
    string? CauseCode,
    double TapMwh,
    double TapNok,
    int TimerSettlement,
    bool HarOperlogMatch,
    string? Rationale);

public sealed record NedetidKategoriSummary(
    string Kategori,
    int Antall,
    double TotalTimer,
    double TotalTapNok);

public sealed record NedetidResponse(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int AntallEvents,
    double TotalNedetidTimer,
    double TotalTapMwh,
    double TotalTapNok,
    IReadOnlyList<NedetidKategoriSummary> KategoriSummaries,
    IReadOnlyList<NedetidEventDto> Events);

public sealed record VaktRoiEventDto(
    NedetidEventDto Event,
    bool ErInnenforVakt,
    bool ErReddbar,
    DateTimeOffset? CounterfactualEndUtc,
    double EkstraTimerSpart,
    double ReddetMwh,
    double ReddetNok,
    double ReddetProduksjon_NOK,
    double ReddetUbalanse_NOK,
    int OverflowTimerInCounterfactual,
    bool OverflowDataMissing,
    string Forklaring);

public sealed record EffektivitetResponse(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int ProduksjonsTimer,
    double SnittEtaPct,
    double SweetSpotEffektKw,
    double SweetSpotEtaPct,
    double SnittSpesifiktVannforbrukM3PerKwh,
    double TotalProduksjonKwh,
    bool DataMissing,
    IReadOnlyList<EffektivitetPunkt> Punkter,
    IReadOnlyList<EffektivitetBin> Bins);

public sealed record EffektivitetPunkt(
    DateTimeOffset TimeUtc,
    double EffektKw,
    double EtaPct,
    double VannforingM3PerS);

public sealed record EffektivitetBin(
    double EffektKwStart,
    double EffektKwMid,
    int Antall,
    double SnittEtaPct);

public sealed record VaktRoiResponse(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    double InstallertEffektMw,
    double SnittSpotprisNokMwh,
    double SnittUbalansetilleggNokMwh,
    double Kapasitetsfaktor,
    int AntallEventsTotalt,
    int AntallReddbareInnenforVakt,
    double TotalReddetMwh,
    double TotalReddetNok,
    double TotalReddetProduksjon_NOK,
    double TotalReddetUbalanse_NOK,
    double SnittEkstraTimerPerEvent,
    IReadOnlyList<VaktRoiEventDto> Events);
