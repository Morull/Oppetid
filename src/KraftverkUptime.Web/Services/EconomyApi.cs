using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KraftverkUptime.Web.Services;

/// <summary>
/// Typed klient mot /api/v1/economy. DTO-ene speiler
/// <c>KraftverkUptime.Modules.Reporting.Economy.EconomyReportDto</c>
/// men er lokale her fordi Web-prosjektet er WASM-frikoblet og kan ikke
/// referere Modules.Reporting direkte. Endringer i kontrakten må holdes
/// i synk begge steder — se NESTE-CHAT-OKONOMI-FANE-PDF.md.
///
/// Klient-side memoization: resultatet bufres per (plantIds, fra, til, kind)
/// med 30 sekunders TTL. To samtidige kall med samme nøkkel deler samme
/// in-flight Task — det fungerer både som cache og request-coalescing.
/// Spec MASTERPLAN-CODE-2026-05-22 § «Ytelse — kjapp gevinst», 2026-05-22.
/// </summary>
public sealed class EconomyApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    public EconomyApi(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>
    /// Henter økonomi-rapport for valgt utvalg anlegg og periode.
    /// <paramref name="plantIds"/> = enkelt-ID, flere ID-er (komma-separert)
    /// eller "all". <paramref name="kind"/> styrer hvordan «forrige periode»
    /// beregnes for trend-pilene.
    /// </summary>
    public Task<EconomyReportDto> GetReportAsync(
        IReadOnlyList<string> plantIds,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string kind = "Custom",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plantIds);
        if (plantIds.Count == 0)
        {
            throw new ArgumentException("plantIds må inneholde minst ett anlegg.", nameof(plantIds));
        }

        var key = BuildCacheKey(plantIds, fromUtc, toUtc, kind);
        var now = DateTimeOffset.UtcNow;

        // Try-get først. Hvis utløpt eller faulted, lag ny entry. AddOrUpdate
        // sørger for atomisk replacement når flere kall kjører samtidig.
        if (_cache.TryGetValue(key, out var existing) && existing.Expires > now && !existing.Task.IsFaulted)
        {
            return existing.Task;
        }

        var newEntry = new CacheEntry(now.Add(CacheTtl), FetchAsync(plantIds, fromUtc, toUtc, kind, ct));
        _cache[key] = newEntry;

        // Opportunistisk opprydning: ved miss går vi gjennom og fjerner
        // utgåtte entries. Cache vokser ikke ubegrenset selv om brukeren
        // veksler periode lenge.
        CleanExpired(now);

        return newEntry.Task;
    }

    private async Task<EconomyReportDto> FetchAsync(
        IReadOnlyList<string> plantIds,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        string kind,
        CancellationToken ct)
    {
        var plantsParam = string.Join(',', plantIds);
        var url = "api/v1/economy"
            + $"?plants={Uri.EscapeDataString(plantsParam)}"
            + $"&from={Uri.EscapeDataString(fromUtc.ToString("o"))}"
            + $"&to={Uri.EscapeDataString(toUtc.ToString("o"))}"
            + $"&kind={Uri.EscapeDataString(kind)}";

        var resp = await _http.GetFromJsonAsync<EconomyReportDto>(url, JsonOptions, ct).ConfigureAwait(false);
        return resp ?? throw new InvalidOperationException("Tom respons fra /economy.");
    }

    private static string BuildCacheKey(
        IReadOnlyList<string> plantIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, string kind)
    {
        // Sortér for å gjøre nøkkelen kanonisk: ["a","b"] og ["b","a"] treffer
        // samme cache-entry siden de gir samme rapport.
        var sortedPlants = string.Join(',', plantIds.OrderBy(p => p, StringComparer.Ordinal));
        return $"{sortedPlants}|{fromUtc:o}|{toUtc:o}|{kind}";
    }

    private void CleanExpired(DateTimeOffset now)
    {
        foreach (var kv in _cache)
        {
            if (kv.Value.Expires <= now)
            {
                _cache.TryRemove(kv.Key, out _);
            }
        }
    }

    private sealed record CacheEntry(DateTimeOffset Expires, Task<EconomyReportDto> Task);
}

// -----------------------------------------------------------------------------
// DTO-speil av Modules.Reporting.Economy.EconomyReportDto. Endringer her må
// matches der; tester i Infrastructure verifiserer struktur, JSON-property-
// navn følger camelCase via JsonOptions.
// -----------------------------------------------------------------------------

public sealed record EconomyReportDto(
    string[] PlantIds,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset PrevFrom,
    DateTimeOffset PrevTo,
    EconomyKpiGroupDto Inntekter,
    EconomyKpiGroupDto Kostnader,
    EconomyKpiGroupDto Resultat,
    PerPlantEconomyDto[] PerPlant);

public sealed record EconomyKpiGroupDto(
    string Title,
    EconomyKpiDto[] Kpis);

public sealed record EconomyKpiDto(
    string Key,
    string Label,
    double Verdi,
    string Enhet,
    double? VerdiForrige,
    double? EndringProsent,
    string GoodDirection);

public sealed record PerPlantEconomyDto(
    string PlantId,
    string PlantName,
    double OppgjorNok,
    double SpotomsetningNok,
    double UbalansekostNok,
    double KaiaKostnadNok,
    double CaptureRate,
    // Sammendrag-felt — Sammendrag-tabellen leser disse fra samme respons
    // istedenfor å fan-out 22+ per-anleggs-kall (Spec MASTERPLAN-CODE-2026-05-22).
    double InstalledCapacityMw,
    double TotalProductionMwh,
    double MerverdiNok,
    double AvailabilityFactor,
    double AvailabilityFactorIeee,
    double ForcedOutageRate,
    double NedetidTimer,
    double NedetidstapNok,
    double ReddetAvVaktNok,
    int AntallEvents,
    int AntallReddbareEvents,
    double? NormalAarsproduksjonGwh,
    // Månedsprofil for månedsvektet normalår-sammenligning (null = flat).
    double[]? MaanedsprofilProsent,
    double GoodHoursPct,
    int ManglerImportHours);
