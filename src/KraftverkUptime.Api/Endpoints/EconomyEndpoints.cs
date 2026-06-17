using System.Globalization;
using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Reporting;
using KraftverkUptime.Modules.Reporting.Economy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Økonomi-rapport-endepunkter (Spec NESTE-CHAT-OKONOMI-FANE-PDF.md):
///   GET /api/v1/economy?plants=&amp;from=&amp;to=&amp;kind=
///
/// Aggregerer per-plant KPI-er fra Settlement, KAIA, Capture rate, Vakt-ROI
/// og Nedetid til en enkelt rapport med trend-piler mot forrige periode.
/// PDF-eksport-endepunktet (<c>/economy/pdf</c>) kommer i Steg 7–8 av spec.
/// </summary>
public static class EconomyEndpoints
{
    public static IEndpointRouteBuilder MapEconomyV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints
            .MapGroup("/api/v{version:apiVersion}/economy")
            .WithTags("Economy")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/", GetReportAsync)
            .WithName("GetEconomyReport")
            .WithSummary("Aggregert økonomi-rapport for valgt utvalg anlegg og periode.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<EconomyReportDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/pdf", GetPdfAsync)
            .WithName("GetEconomyReportPdf")
            .WithSummary("Økonomi-rapport som PDF, klar for nedlasting.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> GetReportAsync(
        string? plants,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? kind,
        IEconomyReportQueryService service,
        IMemoryCache cache,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plants))
        {
            return Results.Problem(
                title: "Manglende anlegg",
                detail: "'plants' må oppgis som komma-separert liste, eller 'all' for hele porteføljen.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!from.HasValue || !to.HasValue)
        {
            return Results.Problem(
                title: "Manglende periode",
                detail: "Både 'from' og 'to' må oppgis (ISO-8601, UTC).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var fromUtc = from.Value.ToUniversalTime();
        var toUtc = to.Value.ToUniversalTime();
        if (toUtc <= fromUtc)
        {
            return Results.Problem(
                title: "Ugyldig periode",
                detail: "'to' må være etter 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParsePeriodKind(kind, out var periodKind))
        {
            return Results.Problem(
                title: "Ugyldig periode-type",
                detail: "'kind' må være en av: Month, Quarter, Year, YearToDate, Custom (case-insensitiv).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var plantIds = ParsePlantsParameter(plants);
        if (plantIds.Count == 0)
        {
            return Results.Problem(
                title: "Tomt anleggs-utvalg",
                detail: "'plants' inneholdt ingen gyldige anleggs-ID-er.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Server-side cache: /economy er tungt (kald ~22 s for "all"; per-anlegg
        // capture-rate bruker lett-variant, og rapport-blobbene deles nå via
        // CachingUptimeReportStore). Oversikt-landingssiden OG Portefølje bruker
        // dette; cachen (DTO-nivå, TTL 3 min) gjør reload/navigasjon momentant.
        // Bevisst INGEN bakgrunns-warmer: en tidligere variant mettet api+azurite
        // og gjorde hele appen tregere.
        var cacheKey = BuildCacheKey(plantIds, fromUtc, toUtc, periodKind);
        if (!cache.TryGetValue(cacheKey, out EconomyReportDto? report) || report is null)
        {
            report = await service.GetAsync(plantIds, fromUtc, toUtc, periodKind, ct).ConfigureAwait(false);
            cache.Set(cacheKey, report, TimeSpan.FromMinutes(3));
        }
        return Results.Ok(report);
    }

    /// <summary>
    /// Kanonisk cache-nøkkel for økonomi-rapporten (DTO-nivå server-cache).
    /// </summary>
    internal static string BuildCacheKey(
        IReadOnlyList<string> plantIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, PeriodKind kind)
        => "economy|"
            + string.Join(',', plantIds.OrderBy(p => p, StringComparer.Ordinal))
            + $"|{fromUtc:o}|{toUtc:o}|{kind}";

    /// <summary>
    /// Plukker ut anleggs-ID-er fra <c>plants</c>-parameteren. Komma-separert
    /// liste; tilfellet <c>"all"</c> håndteres som signal til tjenesten ved at
    /// vi sender den unprosessert videre (tjenesten matcher mot porteføljen).
    /// Tomme tokens og whitespace fjernes.
    /// </summary>
    private static List<string> ParsePlantsParameter(string plants)
    {
        if (string.Equals(plants.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string> { "all" };
        }

        return plants
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool TryParsePeriodKind(string? input, out PeriodKind kind)
    {
        // Default = Custom: hvis kalleren ikke spesifiserer, antar vi at de
        // valgte vilkårlige datoer og vil sammenligne mot lik-lengde-vindu
        // umiddelbart før.
        if (string.IsNullOrWhiteSpace(input))
        {
            kind = PeriodKind.Custom;
            return true;
        }
        return Enum.TryParse(input, ignoreCase: true, out kind);
    }

    // -------------------------------------------------------------------------
    // PDF-endepunkt
    // -------------------------------------------------------------------------

    private static async Task<IResult> GetPdfAsync(
        string? plants,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? kind,
        IEconomyReportQueryService service,
        KraftverkDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plants))
        {
            return Results.Problem(
                title: "Manglende anlegg",
                detail: "'plants' må oppgis som komma-separert liste, eller 'all'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!from.HasValue || !to.HasValue)
        {
            return Results.Problem(
                title: "Manglende periode",
                detail: "Både 'from' og 'to' må oppgis (ISO-8601, UTC).",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var fromUtc = from.Value.ToUniversalTime();
        var toUtc = to.Value.ToUniversalTime();
        if (toUtc <= fromUtc)
        {
            return Results.Problem(
                title: "Ugyldig periode",
                detail: "'to' må være etter 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParsePeriodKind(kind, out var periodKind))
        {
            return Results.Problem(
                title: "Ugyldig periode-type",
                detail: "'kind' må være Month, Quarter, Year, YearToDate, Custom.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var plantIds = ParsePlantsParameter(plants);
        if (plantIds.Count == 0)
        {
            return Results.Problem(
                title: "Tomt anleggs-utvalg", detail: "Ingen gyldige anleggs-ID-er.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Hovedrapport for valgt periode — bygger DTO med trend mot forrige periode.
        var report = await service.GetAsync(plantIds, fromUtc, toUtc, periodKind, ct).ConfigureAwait(false);

        // Plant-navn-lookup for forsiden ("Hele porteføljen" vs "Utvalg: Haukland, Vikesa").
        var plantNamesById = await db.Plants.AsNoTracking()
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct)
            .ConfigureAwait(false);

        // Måneds-trend for forrige 12 måneder, basert på toUtc (den valgte
        // periodens slutt). Hver bøtte = én månedsperiode; vi gjenbruker
        // EconomyReportQueryService og plukker ut Spot/Oppgjør per måned.
        var monthsBack = 12;
        var trendStart = toUtc.AddMonths(-monthsBack);
        var trendBuckets = await BuildTrendAsync(service, report.PlantIds, trendStart, toUtc, ct).ConfigureAwait(false);

        var pdfBytes = EconomyPdfBuilder.Build(
            report,
            trendBuckets.Spot,
            trendBuckets.Oppgjor,
            plantNamesById,
            totalPlantsInPortefolje: plantNamesById.Count,
            generatedAtUtc: DateTimeOffset.UtcNow);

        var fileName = BuildPdfFileName(report, plantNamesById);
        return Results.File(pdfBytes, "application/pdf", fileName);
    }

    /// <summary>
    /// Bygger 12 måneds-bøtter for Spotomsetning og Oppgjør ved å kalle
    /// <see cref="IEconomyReportQueryService.GetAsync"/> en gang per måned.
    /// Kjøres sekvensielt fordi tjenesten internt bruker KraftverkDbContext
    /// (Scoped) som ikke er tråd-sikker. Resultatene sorteres kronologisk
    /// slik at bardiagrammet får venstre→høyre = eldste→nyeste.
    /// </summary>
    private static async Task<(List<EconomyChartRenderer.MonthBucket> Spot, List<EconomyChartRenderer.MonthBucket> Oppgjor)>
        BuildTrendAsync(
            IEconomyReportQueryService service,
            IReadOnlyList<string> plantIds,
            DateTimeOffset trendStart,
            DateTimeOffset trendEnd,
            CancellationToken ct)
    {
        // Bygg månedsgrenser: første-i-måneden fra trendStart frem til siste-i-måneden før trendEnd.
        var months = new List<(DateTimeOffset from, DateTimeOffset to)>();
        var cursor = new DateTimeOffset(trendStart.Year, trendStart.Month, 1, 0, 0, 0, TimeSpan.Zero);
        while (cursor < trendEnd)
        {
            var next = cursor.AddMonths(1);
            months.Add((cursor, next));
            cursor = next;
        }

        var spot = new List<EconomyChartRenderer.MonthBucket>(months.Count);
        var oppgjor = new List<EconomyChartRenderer.MonthBucket>(months.Count);
        foreach (var m in months)
        {
            double spotV = 0, oppgjorV = 0;
            try
            {
                var r = await service.GetAsync(plantIds, m.from, m.to, PeriodKind.Month, ct).ConfigureAwait(false);
                spotV = r.Inntekter.Kpis.FirstOrDefault(k => k.Key == EconomyKpiKeys.Spotomsetning)?.Verdi ?? 0;
                oppgjorV = r.Resultat.Kpis.FirstOrDefault(k => k.Key == EconomyKpiKeys.Oppgjor)?.Verdi ?? 0;
            }
            catch
            {
                // Måneder uten data → 0-søyle i grafen istedenfor å feile hele PDF-en.
            }

            spot.Add(new EconomyChartRenderer.MonthBucket(m.from.Year, m.from.Month, spotV));
            oppgjor.Add(new EconomyChartRenderer.MonthBucket(m.from.Year, m.from.Month, oppgjorV));
        }

        return (Spot: spot, Oppgjor: oppgjor);
    }

    /// <summary>
    /// Filnavn-mønster fra spec: <c>Okonomi_Haukland_2026-04.pdf</c>,
    /// <c>Okonomi_Utvalg-3anlegg_2026-04-01_2026-05-22.pdf</c>,
    /// <c>Okonomi_Hele-portefolje_2026-Q2.pdf</c>.
    /// </summary>
    private static string BuildPdfFileName(
        EconomyReportDto report,
        IReadOnlyDictionary<string, string> plantNamesById)
    {
        var scope = report.PlantIds.Length == plantNamesById.Count
            ? "Hele-portefolje"
            : report.PlantIds.Length == 1
                ? Sanitize(plantNamesById.TryGetValue(report.PlantIds[0], out var n) ? n : report.PlantIds[0])
                : $"Utvalg-{report.PlantIds.Length}anlegg";

        var periodLabel = FormatPeriodLabel(report.From, report.To);
        return $"Okonomi_{scope}_{periodLabel}.pdf";
    }

    /// <summary>
    /// Format-streng for periode-delen av filnavnet. Detekterer hele måneder
    /// (1.04 – 1.05 → "2026-04") og hele kvartaler heuristisk; ellers
    /// brukes ren dato-spenn.
    /// </summary>
    private static string FormatPeriodLabel(DateTimeOffset from, DateTimeOffset to)
    {
        if (from.Day == 1 && to == from.AddMonths(1))
        {
            return from.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        }
        if (from.Day == 1 && to == from.AddMonths(3) && from.Month is 1 or 4 or 7 or 10)
        {
            var q = (from.Month - 1) / 3 + 1;
            return $"{from.Year}-Q{q}";
        }
        return $"{from:yyyy-MM-dd}_{to.AddSeconds(-1):yyyy-MM-dd}";
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(s.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
        return safe.Trim('-');
    }
}
