using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Modules.Reporting.Portefolje;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Portefølje-endepunkt:
///   GET /api/v1/portfolio/kpis?from=&amp;to=
///
/// Returnerer KPI-er for alle anlegg som har en settlement-import som dekker
/// perioden. Anlegg-uavhengig — alle 11 anlegg listes i samme respons.
/// </summary>
public static class PortfolioEndpoints
{
    public static IEndpointRouteBuilder MapPortfolioV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/portfolio")
            .WithTags("Portfolio")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/kpis", GetKpisAsync)
            .WithName("GetPortfolioKpis")
            .WithSummary("Henter KPI-er på tvers av alle anlegg for en gitt periode.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<PortfolioKpiResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/vakt-roi", GetVaktRoiAsync)
            .WithName("GetPortfolioVaktRoi")
            .WithSummary("Aggregert Vakt-ROI på tvers av alle anlegg for en gitt periode.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<PortfolioVaktRoiResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> GetVaktRoiAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        double? kapasitetsfaktor,
        int? topN,
        IPortfolioVaktRoiQueryService service,
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

        var faktor = kapasitetsfaktor ?? 0.5;
        var top = topN ?? 10;

        var response = await service.GetAsync(fromUtc, toUtc, faktor, top, ct).ConfigureAwait(false);
        return Results.Ok(response);
    }

    private static async Task<IResult> GetKpisAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        IPortfolioQueryService service,
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

        var response = await service.GetKpisAsync(fromUtc, toUtc, ct).ConfigureAwait(false);
        return Results.Ok(response);
    }
}
