using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Modules.Reporting.KaiaCost;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// KAIA-kostnad-endepunkter (Spec KAIA-KOSTNAD):
///   GET /api/v1/plants/{plantId}/kaia-cost/{idempotencyKey}
///   GET /api/v1/portfolio/kaia-cost?from=&amp;to=
///
/// Per-rapport-endepunktet identifiserer en spesifikk import via
/// idempotency-nøkkelen; portefølje-endepunktet plukker "nyeste import som
/// dekker perioden" per anlegg for å unngå dobbeltelling ved reimport.
/// </summary>
public static class KaiaCostEndpoints
{
    public static IEndpointRouteBuilder MapKaiaCostV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        // Per anlegg + import-rapport
        var plantGroup = endpoints
            .MapGroup("/api/v{version:apiVersion}/plants/{plantId}/kaia-cost")
            .WithTags("KaiaCost")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        plantGroup.MapGet("/{idempotencyKey}", GetForImportAsync)
            .WithName("GetKaiaCostForImport")
            .WithSummary("KAIA-kostnad (meglerprovisjon + fast avgift) for én settlement-import.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<KaiaCostResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Portefølje-rollup
        var portfolioGroup = endpoints
            .MapGroup("/api/v{version:apiVersion}/portfolio/kaia-cost")
            .WithTags("KaiaCost")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        portfolioGroup.MapGet("/", GetForPortfolioAsync)
            .WithName("GetKaiaCostForPortfolio")
            .WithSummary("KAIA-kostnad for alle anlegg i porteføljen for en gitt periode.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<KaiaCostResult>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> GetForImportAsync(
        string plantId,
        string idempotencyKey,
        IKaiaCostQueryService service,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plantId) || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Results.Problem(
                title: "Manglende parametere",
                detail: "Både 'plantId' og 'idempotencyKey' må oppgis.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await service.GetForImportAsync(plantId, idempotencyKey, ct).ConfigureAwait(false);
        if (result is null)
        {
            return Results.Problem(
                title: "Import ikke funnet",
                detail: $"Ingen settlement-import for plant '{plantId}' med idempotency '{idempotencyKey}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> GetForPortfolioAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        IKaiaCostQueryService service,
        CancellationToken ct)
    {
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

        var rows = await service.GetForPortfolioAsync(fromUtc, toUtc, ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }
}
