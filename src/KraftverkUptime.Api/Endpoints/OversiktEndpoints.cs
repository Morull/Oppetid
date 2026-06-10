using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Api.Contracts;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Core.Time;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.Nedetid;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Endepunkter for Oversikt-landingssiden (driftsleder/daglig-leder-bildet):
///
///   GET /api/v1/oversikt/nedetid-hendelser?from=&amp;to=&amp;limit=20
///
/// Tverranleggs-aggregeringen gjenbruker <see cref="INedetidQueryService"/>
/// per anlegg server-side og slår sammen til én sortert liste, slik at
/// frontend gjør ett kall istedenfor N. KPI-strip og anleggsstatus på
/// Oversikt-siden dekkes av eksisterende /economy-endepunkt og trenger ingen
/// ny server-kode.
/// </summary>
public static class OversiktEndpoints
{
    public static IEndpointRouteBuilder MapOversiktV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/oversikt")
            .WithTags("Oversikt")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/nedetid-hendelser", GetNedetidHendelserAsync)
            .WithName("GetOversiktNedetidHendelser")
            .WithSummary("Siste nedetidshendelser på tvers av alle anlegg, nyeste først.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<OversiktNedetidResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> GetNedetidHendelserAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        INedetidQueryService nedetid,
        KraftverkDbContext db,
        IQueryContext queryContext,
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

        // Beskytt mot at noen ber om hele porteføljens historikk i én rad-liste.
        var take = Math.Clamp(limit ?? 20, 1, 200);

        // Anlegg-listen filtreres av query-context (owner + soft-delete), så vi
        // arver samme tilgangsregler som resten av APIet.
        var plants = await queryContext.Apply(db.Plants.AsQueryable())
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Sekvensielt per anlegg: én DbContext er ikke tråd-trygg, og 11 anlegg
        // over et kort vindu (typisk siste 7 dager) er rimelig. Gjenbruker
        // nøyaktig samme event-grunnlag som /nedetid-siden.
        var all = new List<OversiktNedetidEventDto>();
        foreach (var plant in plants)
        {
            var events = await nedetid.ListEventsAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);
            foreach (var e in events)
            {
                all.Add(new OversiktNedetidEventDto(
                    PlantId: plant.Id,
                    PlantName: plant.Name,
                    StartUtc: e.StartUtc,
                    EndUtc: e.EndUtc,
                    VarighetTimer: e.VarighetTimer,
                    Kategori: e.Category.ToString(),
                    CauseCode: e.CauseCode,
                    TapNok: e.TapNok,
                    HarOperlogMatch: e.HarOperlogMatch,
                    Rationale: e.Rationale));
            }
        }

        var ordered = all
            .OrderByDescending(e => e.StartUtc)
            .Take(take)
            .ToList();

        return Results.Ok(new OversiktNedetidResponse(fromUtc, toUtc, ordered.Count, ordered));
    }
}
