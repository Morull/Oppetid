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

    public async Task<PortfolioKpiResponse> GetPortfolioKpisAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var qs = $"from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}"
               + $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        var resp = await _http
            .GetFromJsonAsync<PortfolioKpiResponse>($"api/v1/portfolio/kpis?{qs}", JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /portfolio/kpis.");
    }

    public async Task<CaptureRateResultDto> GetCaptureRateAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "capture-rate", fromUtc, toUtc, format: null, kapasitetsfaktor: null);
        var resp = await _http.GetFromJsonAsync<CaptureRateResultDto>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /capture-rate.");
    }

    public async Task<IReadOnlyList<MonthlyCaptureRateDto>> GetCaptureRateMonthlyAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "capture-rate/monthly", fromUtc, toUtc, format: null, kapasitetsfaktor: null);
        var resp = await _http.GetFromJsonAsync<List<MonthlyCaptureRateDto>>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? new List<MonthlyCaptureRateDto>();
    }

    public async Task<IReadOnlyList<DailyCaptureRateDto>> GetCaptureRateDailyAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "capture-rate/daily", fromUtc, toUtc, format: null, kapasitetsfaktor: null);
        var resp = await _http.GetFromJsonAsync<List<DailyCaptureRateDto>>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? new List<DailyCaptureRateDto>();
    }

    /// <summary>Henter produksjons-analyse (Hydrogrid-evaluering) for en periode.</summary>
    public async Task<ProduksjonAnalyseDto> GetProduksjonAnalyseAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "produksjon-analyse", fromUtc, toUtc, format: null, kapasitetsfaktor: null);
        var resp = await _http.GetFromJsonAsync<ProduksjonAnalyseDto>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /produksjon-analyse.");
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

public sealed record PortfolioKpiResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int PlantCount,
    IReadOnlyList<PortfolioPlantKpi> Plants);

public sealed record PortfolioPlantKpi(
    string PlantId,
    string PlantName,
    double InstalledCapacityMw,
    int HourCount,
    IReadOnlyDictionary<string, double?> Kpis);

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

public sealed record CaptureRateResultDto(
    double CapturePriceNokMwh,
    double TimesCr,
    double TimesBaselineNokMwh,
    double DagCr,
    double DagBaselineNokMwh,
    double MerverdiNok,
    int AntallTimer,
    int AntallTimerProduksjon,
    int AntallDager,
    int AntallDagerEtterFilter);

public sealed record MonthlyCaptureRateDto(
    int Year,
    int Month,
    CaptureRateResultDto Result);

public sealed record DailyCaptureRateDto(
    DateOnly Date,
    double MwhDay,
    double SpotDayAvgNokMwh,
    double OppnaaddNokMwh,
    double RaCr,
    bool ErFiltrert);

public sealed record ProduksjonAnalyseDto(
    string PlantId,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int AntallTimer,
    int AntallTimerMedPlan,
    int AntallTimerProduksjon,
    int AntallTimerOverlop,
    double TotalElhubMwh,
    double TotalPlanMwh,
    double PlanTreffProsent,
    double AndelProdIToppKvartil,
    double AndelProdIBunnKvartil,
    double KapasitetsutnyttelseProsent,
    double OverlopProsent,
    double HydrogridMerverdiNok,
    double FaktiskMerverdiNok,
    double SnittSpotprisNokMwh,
    bool OverlopDataTilgjengelig,
    IReadOnlyList<ProduksjonHourlyDto> Hourly,
    IReadOnlyList<ProduksjonMonthlyDto> Monthly);

public sealed record ProduksjonHourlyDto(
    DateTimeOffset TimeUtc,
    double? PlanMwh,
    double? ElhubMwh,
    double? SpotprisNokMwh);

public sealed record ProduksjonMonthlyDto(
    int Year,
    int Month,
    double ElhubMwh,
    double PlanMwh,
    int AntallTimerProduksjon,
    int AntallTimerOverlop,
    double KapasitetsutnyttelseProsent,
    double OverlopProsent,
    double PlanTreffProsent,
    double AndelProdIToppKvartil,
    double HydrogridMerverdiNok,
    double FaktiskMerverdiNok,
    double SnittSpotprisNokMwh);

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
