using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Modules.Reporting.DataQuality;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Datakvalitet-endepunkter (SPEC-MVP-HARDENING tiltak C):
///   GET /api/v1/plants/{plantId}/data-quality?from=&amp;to=
///   GET /api/v1/portfolio/data-quality?from=&amp;to=
///
/// Eksponerer DqState-fordelingen som finnes i settlement-importens
/// Classified-rader. Brukes av portefølje-tabell, anleggs-side og
/// filter-toggle på Nedetid/Produksjon.
/// </summary>
public static class DataQualityEndpoints
{
    public static IEndpointRouteBuilder MapDataQualityV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var plantGroup = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/data-quality")
            .WithTags("DataQuality")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        plantGroup.MapGet("/", GetForPlantAsync)
            .WithName("GetPlantDataQuality")
            .WithSummary("Datakvalitets-summary for et anlegg i en periode (god/warning/bad/missing/manglerImport).")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<DataQualitySummary>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var portfolioGroup = endpoints.MapGroup("/api/v{version:apiVersion}/portfolio/data-quality")
            .WithTags("DataQuality")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        portfolioGroup.MapGet("/", GetForPortfolioAsync)
            .WithName("GetPortfolioDataQuality")
            .WithSummary("Datakvalitets-summary for alle anlegg i porteføljen for en periode.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<DataQualitySummary>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> GetForPlantAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IDataQualityQueryService service,
        CancellationToken ct)
    {
        if (!TryValidatePeriod(from, to, out var fromUtc, out var toUtc, out var problem))
        {
            return problem!;
        }
        var summary = await service.GetSummaryAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
        return summary is null
            ? Results.Problem(title: "Anlegg ikke funnet",
                detail: $"Plant '{plantId}' eksisterer ikke.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(summary);
    }

    private static async Task<IResult> GetForPortfolioAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        IDataQualityQueryService service,
        CancellationToken ct)
    {
        if (!TryValidatePeriod(from, to, out var fromUtc, out var toUtc, out var problem))
        {
            return problem!;
        }
        var rows = await service.GetSummariesForAllPlantsAsync(fromUtc, toUtc, ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static bool TryValidatePeriod(
        DateTimeOffset? from, DateTimeOffset? to,
        out DateTimeOffset fromUtc, out DateTimeOffset toUtc,
        out IResult? problem)
    {
        fromUtc = default; toUtc = default; problem = null;
        if (!from.HasValue || !to.HasValue)
        {
            problem = Results.Problem(title: "Manglende periode",
                detail: "Både 'from' og 'to' må oppgis (ISO-8601, UTC).",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
        fromUtc = from.Value.ToUniversalTime();
        toUtc = to.Value.ToUniversalTime();
        if (toUtc <= fromUtc)
        {
            problem = Results.Problem(title: "Ugyldig periode",
                detail: "'to' må være etter 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
        return true;
    }
}
