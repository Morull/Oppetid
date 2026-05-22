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
        var url = BuildUrl(plantId, "nedetid", fromUtc, toUtc, format: null);
        var resp = await _http.GetFromJsonAsync<NedetidResponse>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /nedetid.");
    }

    public async Task<VaktRoiResponse> GetVaktRoiAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        string? vaktStartLokal = null,
        string? vaktSluttLokal = null,
        string? oppmoteLokal = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "vakt-roi", fromUtc, toUtc, format: null,
            vaktStartLokal: vaktStartLokal,
            vaktSluttLokal: vaktSluttLokal,
            oppmoteLokal: oppmoteLokal);
        var resp = await _http.GetFromJsonAsync<VaktRoiResponse>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /vakt-roi.");
    }

    public async Task<EffektivitetResponse> GetEffektivitetAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "effektivitet", fromUtc, toUtc, format: null);
        var resp = await _http.GetFromJsonAsync<EffektivitetResponse>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /effektivitet.");
    }

    /// <summary>
    /// Episode-analyse — underytende intervaller gruppert og rangert.
    /// Spec FORBEDRINGSFORSLAG-EFFEKTIVITET.md Del 3.
    /// </summary>
    public async Task<EpisodeAnalysisResult> GetEpisoderAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        double? terskelPp = null, int? gapIntervaller = null,
        EpisodeReferanseTyp referanse = EpisodeReferanseTyp.Baseline,
        CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "effektivitet/episoder", fromUtc, toUtc, format: null);
        var extra = new List<string>();
        if (terskelPp.HasValue) extra.Add($"terskelPp={terskelPp.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (gapIntervaller.HasValue) extra.Add($"gapIntervaller={gapIntervaller.Value}");
        if (referanse == EpisodeReferanseTyp.SweetSpot) extra.Add("referanse=sweetspot");
        if (extra.Count > 0) url += (url.Contains('?') ? "&" : "?") + string.Join("&", extra);
        var resp = await _http.GetFromJsonAsync<EpisodeAnalysisResult>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /effektivitet/episoder.");
    }

    /// <summary>
    /// Anleggssammenligning på effektivitet — én rad per anlegg.
    /// Spec FORBEDRINGSFORSLAG-EFFEKTIVITET.md Del 4.1.
    /// </summary>
    public async Task<IReadOnlyList<EffektivitetPortfolioRad>> GetEffektivitetPortfolioAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = $"api/v1/effektivitet/portefolje?from={Uri.EscapeDataString(fromUtc.ToString("o"))}&to={Uri.EscapeDataString(toUtc.ToString("o"))}";
        var resp = await _http.GetFromJsonAsync<List<EffektivitetPortfolioRad>>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? new List<EffektivitetPortfolioRad>();
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

    /// <summary>Lister vakt-overrides for et anlegg.</summary>
    public async Task<IReadOnlyList<VaktOverrideDto>> ListVaktOverridesAsync(
        string plantId, CancellationToken ct = default)
    {
        var url = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/vakt-overrides";
        var items = await _http.GetFromJsonAsync<List<VaktOverrideDto>>(url, JsonOptions, ct)
            .ConfigureAwait(false);
        return items ?? (IReadOnlyList<VaktOverrideDto>)Array.Empty<VaktOverrideDto>();
    }

    /// <summary>
    /// Setter override for én vakt-event. Classification: "Auto",
    /// "HaddeOverlop" eller "IkkeOverlop".
    /// </summary>
    public async Task<VaktOverrideDto> UpsertVaktOverrideAsync(
        string plantId, DateTimeOffset eventStartUtc, string classification,
        string? comment,
        DateTimeOffset? actualEndOverrideUtc = null,
        CancellationToken ct = default)
    {
        var url = $"api/v1/plants/{Uri.EscapeDataString(plantId)}/vakt-overrides";
        var body = new { eventStartUtc, classification, comment, actualEndOverrideUtc };
        var resp = await _http.PutAsJsonAsync(url, body, JsonOptions, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<VaktOverrideDto>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Tom respons fra upsert-vakt-override.");
    }

    /// <summary>
    /// Aggregert Vakt-ROI på tvers av alle anlegg. Brukes av portefølje-
    /// dashboardet på <c>/vakt-roi</c> (uten plantId).
    /// </summary>
    public async Task<PortfolioVaktRoiResponse> GetPortfolioVaktRoiAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int topN = 10,
        string? vaktStartLokal = null,
        string? vaktSluttLokal = null,
        string? oppmoteLokal = null,
        CancellationToken ct = default)
    {
        var qs = $"from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}"
               + $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}"
               + $"&topN={topN}";
        if (!string.IsNullOrWhiteSpace(vaktStartLokal))
            qs += $"&vaktStartLokal={Uri.EscapeDataString(vaktStartLokal)}";
        if (!string.IsNullOrWhiteSpace(vaktSluttLokal))
            qs += $"&vaktSluttLokal={Uri.EscapeDataString(vaktSluttLokal)}";
        if (!string.IsNullOrWhiteSpace(oppmoteLokal))
            qs += $"&oppmoteLokal={Uri.EscapeDataString(oppmoteLokal)}";
        var resp = await _http
            .GetFromJsonAsync<PortfolioVaktRoiResponse>($"api/v1/portfolio/vakt-roi?{qs}", JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /portfolio/vakt-roi.");
    }

    public async Task<CaptureRateResultDto> GetCaptureRateAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "capture-rate", fromUtc, toUtc, format: null);
        var resp = await _http.GetFromJsonAsync<CaptureRateResultDto>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /capture-rate.");
    }

    public async Task<IReadOnlyList<MonthlyCaptureRateDto>> GetCaptureRateMonthlyAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "capture-rate/monthly", fromUtc, toUtc, format: null);
        var resp = await _http.GetFromJsonAsync<List<MonthlyCaptureRateDto>>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? new List<MonthlyCaptureRateDto>();
    }

    public async Task<IReadOnlyList<DailyCaptureRateDto>> GetCaptureRateDailyAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "capture-rate/daily", fromUtc, toUtc, format: null);
        var resp = await _http.GetFromJsonAsync<List<DailyCaptureRateDto>>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? new List<DailyCaptureRateDto>();
    }

    /// <summary>Henter produksjons-analyse (Hydrogrid-evaluering) for en periode.</summary>
    public async Task<ProduksjonAnalyseDto> GetProduksjonAnalyseAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "produksjon-analyse", fromUtc, toUtc, format: null);
        var resp = await _http.GetFromJsonAsync<ProduksjonAnalyseDto>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /produksjon-analyse.");
    }

    /// <summary>Henter datakvalitets-summary for et anlegg (SPEC-MVP-HARDENING C).</summary>
    public async Task<DataQualitySummaryDto?> GetDataQualityAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var url = BuildUrl(plantId, "data-quality", fromUtc, toUtc, format: null);
        try
        {
            return await _http.GetFromJsonAsync<DataQualitySummaryDto>(url, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // anlegget finnes ikke — UI kan vise "—"
        }
    }

    /// <summary>Henter datakvalitets-summary for hele porteføljen.</summary>
    public async Task<IReadOnlyList<DataQualitySummaryDto>> GetPortfolioDataQualityAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var qs = $"from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}"
               + $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        var resp = await _http
            .GetFromJsonAsync<List<DataQualitySummaryDto>>(
                $"api/v1/portfolio/data-quality?{qs}", JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? new List<DataQualitySummaryDto>();
    }

    /// <summary>Henter import-completeness-matrisen (SPEC-IMPORT-COMPLETENESS).</summary>
    public async Task<DataCompletenessMatrixDto> GetDataStatusMatrixAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var qs = $"from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}"
               + $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        var resp = await _http
            .GetFromJsonAsync<DataCompletenessMatrixDto>(
                $"api/v1/data-status/matrix?{qs}", JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /data-status/matrix.");
    }

    /// <summary>Henter overdue-listen.</summary>
    public async Task<IReadOnlyList<MissingImportDto>> GetDataStatusOverdueAsync(
        CancellationToken ct = default)
    {
        var resp = await _http
            .GetFromJsonAsync<List<MissingImportDto>>(
                "api/v1/data-status/overdue", JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? new List<MissingImportDto>();
    }

    /// <summary>Henter ukentlig sammendrag — brukes på dashboard-toppen.</summary>
    public async Task<DataCompletenessSummaryDto> GetDataStatusSummaryAsync(
        CancellationToken ct = default)
    {
        var resp = await _http
            .GetFromJsonAsync<DataCompletenessSummaryDto>(
                "api/v1/data-status/summary", JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /data-status/summary.");
    }

    /// <summary>Henter status for hot-folder-watcheren (kø + siste prosesserte filer).</summary>
    public async Task<HotFolderStatusDto?> GetHotFolderStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http
                .GetFromJsonAsync<HotFolderStatusDto>(
                    "api/v1/hot-folder/queue", JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null; // hot-folder kanskje ikke aktiv — banneren skjuler seg
        }
    }

    /// <summary>Trigger manuell scan av hot-folder-mappa ("Skann nå"-knapp).</summary>
    public async Task<HotFolderScanResultDto?> TriggerHotFolderScanAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync(
                new Uri("api/v1/hot-folder/scan-now", UriKind.Relative), content: null, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<HotFolderScanResultDto>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Flytt karantene-filer tilbake til hot-folder for ny prosessering.</summary>
    public async Task<HotFolderRetryResultDto?> RetryQuarantineAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync(
                new Uri("api/v1/hot-folder/retry-quarantine", UriKind.Relative), content: null, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<HotFolderRetryResultDto>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recovery: flytt alle filer fra done/ tilbake til import-rot og nullstill
    /// dedup-cache. Brukes etter at azurite blob-storage er nullstilt for å
    /// regenerere rapport-blobs fra bevarte CSV/xlsx-filer på disk.
    /// </summary>
    public async Task<HotFolderRetryResultDto?> ReimportDoneAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsync(
                new Uri("api/v1/hot-folder/reimport-done", UriKind.Relative), content: null, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadFromJsonAsync<HotFolderRetryResultDto>(JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Henter detaljert diagnose for en karantenert fil — brukt av "Diagnostikk"-
    /// modal i UI for å vise hvorfor filen ble avvist.
    /// </summary>
    public async Task<HotFolderDiagnoseResultDto?> DiagnoseQuarantineAsync(
        string fileName, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/v1/hot-folder/quarantine/{Uri.EscapeDataString(fileName)}/diagnose";
            return await _http.GetFromJsonAsync<HotFolderDiagnoseResultDto>(url, JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Marker en (plant, source, period)-celle som manuelt verifisert komplett.
    /// Returnerer true ved suksess. Brukes når drifts-leder har sjekket SCADA HMI
    /// og bekreftet at "delvis"-status ikke skyldes manglende data.
    /// </summary>
    public async Task<bool> SetCompletenessOverrideAsync(
        string plantId, string sourceType, DateTimeOffset period, string? reason,
        CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync(
                "api/v1/data-status/override",
                new SetOverrideRequestDto(plantId, sourceType, period, reason),
                JsonOptions, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Fjern manuell overstyring så cellen returnerer til auto-status.</summary>
    public async Task<bool> RemoveCompletenessOverrideAsync(
        string plantId, string sourceType, DateTimeOffset period,
        CancellationToken ct = default)
    {
        try
        {
            var url =
                $"api/v1/data-status/override?plantId={Uri.EscapeDataString(plantId)}" +
                $"&sourceType={Uri.EscapeDataString(sourceType)}" +
                $"&period={Uri.EscapeDataString(period.ToString("o"))}";
            var resp = await _http.DeleteAsync(new Uri(url, UriKind.Relative), ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Lister nylige importer (default siste 24 timer) — brukt av auto-import-infobar.</summary>
    public async Task<IReadOnlyList<RecentImportDto>> GetRecentImportsAsync(
        int hours = 24, int limit = 50, CancellationToken ct = default)
    {
        var resp = await _http
            .GetFromJsonAsync<List<RecentImportDto>>(
                $"api/v1/data-status/recent?hours={hours}&limit={limit}", JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? new List<RecentImportDto>();
    }

    /// <summary>Lister alle expectations for et anlegg.</summary>
    public async Task<IReadOnlyList<DataSourceExpectationDto>> ListExpectationsAsync(
        string plantId, CancellationToken ct = default)
    {
        var resp = await _http
            .GetFromJsonAsync<List<DataSourceExpectationDto>>(
                $"api/v1/plants/{Uri.EscapeDataString(plantId)}/data-source-expectations", JsonOptions, ct)
            .ConfigureAwait(false);
        return resp ?? new List<DataSourceExpectationDto>();
    }

    /// <summary>Upsert expectation for et anlegg + kilde-type.</summary>
    public async Task<DataSourceExpectationDto> UpsertExpectationAsync(
        string plantId, string sourceType, bool isActive, int expectedLagDays,
        string? cadence = null, double? completionThresholdPct = null,
        CancellationToken ct = default)
    {
        var body = new
        {
            isActive,
            expectedLagDays,
            cadence = cadence ?? "monthly",
            completionThresholdPct,
        };
        var resp = await _http.PutAsJsonAsync(
            $"api/v1/plants/{Uri.EscapeDataString(plantId)}/data-source-expectations/{Uri.EscapeDataString(sourceType)}",
            body, JsonOptions, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<DataSourceExpectationDto>(JsonOptions, ct)
            .ConfigureAwait(false);
        return dto ?? throw new InvalidOperationException("Tom respons fra upsert.");
    }

    /// <summary>Bygger nedlastings-URL for CSV-eksport (åpnes direkte i ny fane).</summary>
    public Uri BuildCsvUri(string plantId, string endpoint, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var baseAddress = _http.BaseAddress ?? throw new InvalidOperationException("HttpClient mangler BaseAddress.");
        var rel = BuildUrl(plantId, endpoint, fromUtc, toUtc, format: "csv");
        return new Uri(baseAddress, rel);
    }

    private static string BuildUrl(string plantId, string endpoint,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, string? format,
        string? vaktStartLokal = null, string? vaktSluttLokal = null, string? oppmoteLokal = null)
    {
        var qs = $"from={Uri.EscapeDataString(fromUtc.UtcDateTime.ToString("o"))}"
               + $"&to={Uri.EscapeDataString(toUtc.UtcDateTime.ToString("o"))}";
        if (!string.IsNullOrEmpty(format))
        {
            qs += $"&format={Uri.EscapeDataString(format)}";
        }
        if (!string.IsNullOrWhiteSpace(vaktStartLokal))
            qs += $"&vaktStartLokal={Uri.EscapeDataString(vaktStartLokal)}";
        if (!string.IsNullOrWhiteSpace(vaktSluttLokal))
            qs += $"&vaktSluttLokal={Uri.EscapeDataString(vaktSluttLokal)}";
        if (!string.IsNullOrWhiteSpace(oppmoteLokal))
            qs += $"&oppmoteLokal={Uri.EscapeDataString(oppmoteLokal)}";
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
    string Forklaring,
    bool PlanDataPartial = false);

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

// Portefølje Vakt-ROI -- speiler IPortfolioVaktRoiQueryService-kontrakten.
public sealed record PortfolioVaktRoiResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int PlantCount,
    int PlantsWithData,
    double TotalReddetNok,
    int TotalReddbareEvents,
    int TotalEvents,
    IReadOnlyList<PortfolioVaktRoiPlantSummary> PerPlant,
    IReadOnlyList<PortfolioVaktRoiTopEvent> TopEvents,
    IReadOnlyList<PortfolioVaktRoiMonthlyPoint> MonthlyTrend);

public sealed record PortfolioVaktRoiPlantSummary(
    string PlantId,
    string PlantName,
    double InstalledCapacityMw,
    double ReddetNok,
    double ReddetProduksjon_NOK,
    double ReddetUbalanse_NOK,
    int ReddbareEvents,
    int TotaleEvents);

public sealed record PortfolioVaktRoiTopEvent(
    string PlantId,
    string PlantName,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double VarighetTimer,
    string Kategori,
    string? CauseCode,
    double ReddetNok,
    double ReddetProduksjon_NOK,
    double ReddetUbalanse_NOK,
    double EkstraTimerSpart);

public sealed record PortfolioVaktRoiMonthlyPoint(
    int Year,
    int Month,
    double TotalReddetNok,
    int ReddbareEvents);

public sealed record VaktOverrideDto(
    string PlantId,
    DateTimeOffset EventStartUtc,
    string Classification,
    string? Comment,
    DateTimeOffset SetAt,
    string? SetBy,
    DateTimeOffset? ActualEndOverrideUtc);

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
    double VannforingM3PerS,
    PunktKlassifisering Klassifisering);

/// <summary>
/// Klassifisering av et 15-min-intervall — speiler server-side enum. Spec
/// NESTE-CHAT-EFFEKTIVITET-15MIN.md.
/// </summary>
public enum PunktKlassifisering
{
    Genuine = 0,
    Transition = 1,
}

public sealed record EffektivitetBin(
    double EffektKwStart,
    double EffektKwMid,
    int Antall,
    double SnittEtaPct);

// Episode-analyse-DTOer — speiler Modules.Reporting.Effektivitet.
// Spec FORBEDRINGSFORSLAG-EFFEKTIVITET.md Del 3.

public sealed record EpisodeAnalysisResult(
    IReadOnlyList<UnderytendeEpisode> Episoder,
    IReadOnlyList<EffektBaandAggregat> AggregatPerEffektBaand,
    double TotalTaptMwh,
    double TotalTaptNok,
    int AntallGenuineIntervaller,
    int AntallUnderytendeIntervaller,
    bool ManglerSpotpriser);

public sealed record UnderytendeEpisode(
    DateTimeOffset StartUtc,
    DateTimeOffset SluttUtc,
    int AntallIntervaller,
    double VarighetTimer,
    double SnittDeltaEtaPp,
    double EffektMinKw,
    double EffektMaksKw,
    double FaktiskProduksjonMwh,
    double TaptMwh,
    double TaptNok,
    bool TaptNokErEstimat,
    IReadOnlyList<EpisodeIntervall> Intervaller);

public sealed record EpisodeIntervall(
    DateTimeOffset TimeUtc,
    double EffektKw,
    double EtaPct,
    double VannforingM3PerS,
    double ReferanseEtaPct,
    double DeltaEtaPp);

public sealed record EffektBaandAggregat(
    double EffektKwStart,
    double EffektKwSlutt,
    int AntallEpisoder,
    double TotalVarighetTimer,
    double SnittDeltaEtaPp,
    double TaptMwh,
    double TaptNok);

public enum EpisodeReferanseTyp
{
    Baseline = 0,
    SweetSpot = 1,
}

public sealed record EffektivitetPortfolioRad(
    string PlantId,
    string PlantName,
    bool DataMangler,
    double SnittEtaPct,
    double SweetSpotEffektKw,
    double SvfM3PerKwh,
    double TotalProduksjonMwh,
    int AntallEpisoder,
    double TotalTaptMwh,
    double TotalTaptNok);

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
    double AndelTimerProdIToppKvartil,
    double AndelTimerProdIBunnKvartil,
    double KapasitetsutnyttelseProsent,
    double OverlopProsent,
    double HydrogridMerverdiNok,
    double FaktiskMerverdiNok,
    double SnittSpotprisNokMwh,
    bool OverlopDataTilgjengelig,
    IReadOnlyList<ProduksjonHourlyDto> Hourly,
    IReadOnlyList<ProduksjonMonthlyDto> Monthly,
    double SpotbudTreffProsent = 0,
    int AntallTimerMedSpotbud = 0);

public sealed record ProduksjonHourlyDto(
    DateTimeOffset TimeUtc,
    double? PlanMwh,
    double? ElhubMwh,
    double? SpotprisNokMwh,
    double? RkPrisNokMwh = null,
    bool HarOverlop = false,
    double UbalanseKostNok = 0,
    double? SpotbudMwh = null);

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
    double AndelProdIBunnKvartil,
    double AndelTimerProdIToppKvartil,
    double AndelTimerProdIBunnKvartil,
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
    int AntallEventsTotalt,
    int AntallReddbareInnenforVakt,
    double TotalReddetMwh,
    double TotalReddetNok,
    double TotalReddetProduksjon_NOK,
    double TotalReddetUbalanse_NOK,
    double SnittEkstraTimerPerEvent,
    IReadOnlyList<VaktRoiEventDto> Events,
    string VaktStartLokal = "15:00",
    string VaktSluttLokal = "07:00",
    string OppmoteLokal = "08:00");

// --- DataQuality (SPEC-MVP-HARDENING tiltak C) -------------------------------

public sealed record DataQualitySummaryDto(
    string PlantId,
    string PlantName,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int TotalHours,
    int GoodHours,
    int WarningHours,
    int BadHours,
    int MissingHours,
    int ManglerImportHours,
    double GoodPct,
    double DekningPct,
    IReadOnlyList<DataQualityIssueDto> TopIssues);

public sealed record DataQualityIssueDto(
    DateTimeOffset TimeUtc,
    string State,
    string Reason);

// --- DataCompleteness (SPEC-IMPORT-COMPLETENESS) ----------------------------

public sealed record DataCompletenessMatrixDto(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<string> PlantIds,
    IReadOnlyList<string> SourceTypes,
    IReadOnlyList<DateTimeOffset> Periods,
    IReadOnlyList<DataCompletenessCellDto> Cells);

public sealed record DataCompletenessCellDto(
    string PlantId,
    string SourceType,
    DateTimeOffset Period,
    string Status,
    DateTimeOffset? LastImportedAt,
    double? CoveragePct,
    int ImportCount,
    DateTimeOffset? ImportPeriodFromUtc = null,
    DateTimeOffset? ImportPeriodToUtc = null,
    int? RowsImported = null,
    string? FileName = null,
    string? Notes = null,
    double? Threshold = null,
    bool IsManuallyOverridden = false,
    string? OverrideReason = null,
    DateTimeOffset? OverriddenAtUtc = null,
    string? OverriddenByUserId = null);

public sealed record MissingImportDto(
    string PlantId,
    string SourceType,
    DateTimeOffset PeriodFromUtc,
    int DaysOverdue);

public sealed record DataCompletenessSummaryDto(
    int TotalExpected,
    int Complete,
    int Partial,
    int Pending,
    int Overdue,
    IReadOnlyList<MissingImportDto> TopOverdue);

public sealed record DataSourceExpectationDto(
    string PlantId,
    string SourceType,
    string Cadence,
    int ExpectedLagDays,
    bool IsActive,
    DateTimeOffset? ActivatedAtUtc,
    DateTimeOffset? DeactivatedAtUtc,
    double CompletionThresholdPct);

public sealed record RecentImportDto(
    Guid ImportId,
    string PlantId,
    string SourceType,
    DateTimeOffset PeriodFromUtc,
    DateTimeOffset PeriodToUtc,
    DateTimeOffset ImportedAtUtc,
    string? FileName,
    int? RowsImported,
    double? CoveragePct,
    string? UserId);

// --- HotFolder (SPEC-AUTO-IMPORT-FOLDER) ----------------------------------

public sealed record HotFolderStatusDto(
    bool Enabled,
    string RootPath,
    IReadOnlyList<HotFolderQueueEntryDto> Waiting,
    IReadOnlyList<HotFolderRecentEntryDto> Recent);

public sealed record HotFolderQueueEntryDto(
    string FilePath,
    string FileName,
    long FileSize,
    DateTimeOffset DetectedAtUtc,
    string Status); // WAITING / PROCESSING

public sealed record HotFolderRecentEntryDto(
    string FileName,
    string? PlantId,
    string? SourceType,
    string Status, // OK / QUARANTINE / DUPLICATE
    DateTimeOffset ProcessedAtUtc,
    string? Notes);

public sealed record HotFolderScanResultDto(
    bool Triggered,
    string Message,
    int WaitingBefore);

public sealed record HotFolderRetryResultDto(
    int FilesMoved,
    string Message);

/// <summary>Request-body for POST /api/v1/data-status/override.</summary>
public sealed record SetOverrideRequestDto(
    string PlantId,
    string SourceType,
    DateTimeOffset Period,
    string? Reason);

public sealed record HotFolderDiagnoseResultDto(
    string FileName,
    long FileSizeBytes,
    DateTimeOffset QuarantinedAtUtc,
    string? ErrorMessage,
    bool HasDetailedDiagnostics,
    DetectionDiagnosticsDto? Diagnostics);

public sealed record DetectionDiagnosticsDto(
    string FileName,
    long FileSizeBytes,
    string Extension,
    string? DetectedSourceType,
    string? ResolvedPlantId,
    IReadOnlyList<string> Attempts,
    IReadOnlyList<string>? SheetNames,
    IReadOnlyDictionary<string, string> SheetTitleCells,
    string? HeaderLine,
    IReadOnlyDictionary<string, int>? ScadaPrefixCounts,
    IReadOnlyDictionary<string, int>? OperlogStations);
