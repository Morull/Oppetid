using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.Produksjon;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Endepunkter for produksjons-analyse:
///   GET /api/v1/plants/{plantId}/produksjon-analyse?from=&amp;to=
///
/// Bruker eksisterende settlement-data (KAIA-eksport) og besvarer:
///   - Hvor godt fulgte vi Hydrogrids plan?
///   - Hvor stor andel av produksjonen leveres i topp-prisperioder?
///   - Hvor mye merverdi gir Hydrogrids plan vs. flat baseline?
///   - Forbedring/forverring over tid (måneds-trend).
/// </summary>
public static class ProduksjonEndpoints
{
    public static IEndpointRouteBuilder MapProduksjonV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/produksjon-analyse")
            .WithTags("Produksjon")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/", GetAsync)
            .WithName("GetProduksjonAnalyse")
            .WithSummary("Hydrogrid-plan-evaluering, andel produksjon i topp-prisperioder, måneds-trend.")
            .AllowAnonymous()
            .Produces<ProduksjonAnalyseResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IProduksjonAnalyseService service,
        KraftverkDbContext db,
        IQueryContext queryContext,
        CancellationToken ct)
    {
        if (!from.HasValue || !to.HasValue)
        {
            return Results.Problem(title: "Manglende periode",
                detail: "Både 'from' og 'to' må oppgis (ISO-8601, UTC).",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var fromUtc = from.Value.ToUniversalTime();
        var toUtc = to.Value.ToUniversalTime();
        if (toUtc <= fromUtc)
        {
            return Results.Problem(title: "Ugyldig periode",
                detail: "'to' må være etter 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var plantExists = await queryContext.Apply(db.Plants.AsQueryable())
            .AnyAsync(p => p.Id == plantId, ct).ConfigureAwait(false);
        if (!plantExists)
        {
            return Results.Problem(title: "Anlegg ikke funnet",
                detail: $"Plant '{plantId}' eksisterer ikke.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var result = await service.GetAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
        return Results.Ok(result);
    }
}
