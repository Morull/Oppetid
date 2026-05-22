using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Reporting;
using KraftverkUptime.Modules.Reporting.Effektivitet;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Effektivitets-endepunkt:
///   GET /api/v1/plants/{plantId}/effektivitet?from=&amp;to=
///
/// Anlegg-uavhengig — fungerer for alle 11 plants så lenge SCADA-rollene
/// <c>GeneratorActivePower</c>, <c>TurbineEfficiency</c> og <c>TurbineWaterFlow</c>
/// er konfigurert i <c>core.signal_map</c>.
/// </summary>
public static class EffektivitetEndpoints
{
    public static IEndpointRouteBuilder MapEffektivitetV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/effektivitet")
            .WithTags("Effektivitet")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        // Portefølje-oversikt på tvers av anlegg — egen rot.
        var portfolioGroup = endpoints.MapGroup("/api/v{version:apiVersion}/effektivitet")
            .WithTags("Effektivitet")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        portfolioGroup.MapGet("/portefolje", GetPortfolioAsync)
            .WithName("GetEffektivitetPortefolje")
            .WithSummary("Anleggssammenligning: én rad per anlegg med snitt-η, sweet-spot, tapt verdi.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<EffektivitetPortfolioResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/", GetAsync)
            .WithName("GetEffektivitet")
            .WithSummary("Henter η(P)-kurve, sweet-spot og spesifikt vannforbruk for et anlegg.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<EffektivitetResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Episode-analyse — flagger underytende intervaller og slår dem sammen
        // til episoder rangert på tapt verdi. Spec FORBEDRINGSFORSLAG-EFFEKTIVITET.md.
        group.MapGet("/episoder", GetEpisoderAsync)
            .WithName("GetEffektivitetEpisoder")
            .WithSummary("Underytelse-episoder rangert på tapt verdi (NOK).")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<EpisodeAnalysisResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> GetPortfolioAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        EffektivitetPortfolioQueryService service,
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
            return Results.Problem(title: "Ugyldig periode", statusCode: StatusCodes.Status400BadRequest);
        }
        var rows = await service.GetAsync(fromUtc, toUtc, ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetEpisoderAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        double? terskelPp,
        int? gapIntervaller,
        string? referanse,
        EffektivitetEpisodeQueryService episodeService,
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
                statusCode: StatusCodes.Status400BadRequest);
        }

        var plantExists = await queryContext.Apply(db.Plants.AsQueryable())
            .AnyAsync(p => p.Id == plantId, ct).ConfigureAwait(false);
        if (!plantExists)
        {
            return Results.Problem(
                title: "Anlegg ikke funnet",
                detail: $"Plant '{plantId}' eksisterer ikke.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Default-opsjoner i analyseren matcher Spec FORBEDRINGSFORSLAG-EFFEKTIVITET.md.
        // Lar query-parametere overstyre for UI-sliders + referanse-toggle.
        var referanseTyp = string.Equals(referanse, "sweetspot", StringComparison.OrdinalIgnoreCase)
            ? EpisodeReferanseTyp.SweetSpot
            : EpisodeReferanseTyp.Baseline;
        var opsjoner = (terskelPp.HasValue || gapIntervaller.HasValue
                        || referanseTyp != EpisodeReferanseTyp.Baseline)
            ? new EpisodeAnalyseOpsjoner(
                DeltaEtaTerskelPp: terskelPp ?? -2.0,
                TillattGapIntervaller: gapIntervaller ?? 1,
                Referanse: referanseTyp)
            : null;

        var result = await episodeService.GetAsync(plantId, fromUtc, toUtc, opsjoner, ct)
            .ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IEffectivityQueryService service,
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

        var plantExists = await queryContext.Apply(db.Plants.AsQueryable())
            .AnyAsync(p => p.Id == plantId, ct).ConfigureAwait(false);
        if (!plantExists)
        {
            return Results.Problem(
                title: "Anlegg ikke funnet",
                detail: $"Plant '{plantId}' eksisterer ikke.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var response = await service.GetAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
        return Results.Ok(response);
    }
}
