using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Reporting;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Tilsig-basert estimering av "ville-vært-overløp" for et vindu.
/// Brukes som alternativ/sammenligning til SCADA-direkte overflow-deteksjon.
///
/// API:
///   GET /api/v1/plants/{plantId}/inflow-estimate?from=...&amp;to=...
/// </summary>
public static class InflowEstimateEndpoints
{
    public static IEndpointRouteBuilder MapInflowEstimateV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/inflow-estimate")
            .WithTags("InflowEstimate")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/", GetAsync)
            .WithName("GetInflowOverflowEstimate")
            .WithSummary("Estimerer 'ville-overflow'-timer fra tilsig + ledig kapasitet i magasinet.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<InflowEstimateResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        InflowOverflowQueryService service,
        CancellationToken ct)
    {
        if (!from.HasValue || !to.HasValue)
            return Results.Problem(title: "Manglende periode", statusCode: 400);
        if (to.Value <= from.Value)
            return Results.Problem(title: "Ugyldig periode", statusCode: 400);

        var resp = await service.EstimateAsync(plantId, from.Value.ToUniversalTime(), to.Value.ToUniversalTime(), ct)
            .ConfigureAwait(false);
        return Results.Ok(resp);
    }
}
