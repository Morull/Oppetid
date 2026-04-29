using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Api.Paging;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Paging;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Plant-endepunkter for liste, detaljer og admin-redigering.
///   GET    /api/v1/plants               — paginert liste
///   GET    /api/v1/plants/{plantId}     — detaljer
///   PUT    /api/v1/plants/{plantId}     — oppdater navn, type, capacity, timezone
///
/// PUT er anlegg-uavhengig — fungerer for alle 11 anlegg uten spesial-casing.
/// Politikken er <see cref="AuthorizationPolicies.PlantAdmin"/> (i v1: open).
/// </summary>
public static class PlantsEndpoints
{
    public static IEndpointRouteBuilder MapPlantsV1(this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants")
            .WithTags("Plants")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .RequireAuthorization(AuthorizationPolicies.PlantReader);

        group.MapGet("/", ListAsync);
        group.MapGet("/{plantId}", GetByIdAsync)
            .WithName("GetPlant")
            .WithSummary("Henter detaljer for ett anlegg.");
        group.MapPut("/{plantId}", UpdateAsync)
            .WithName("UpdatePlant")
            .WithSummary("Oppdaterer navn, type, kapasitet og tidssone for et anlegg.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin);

        return endpoints;
    }

    // ---- GET /api/v1/plants ----------------------------------------------

    private static async Task<IResult> ListAsync(
        KraftverkDbContext db,
        IQueryContext queryContext,
        IOptions<PaginationOptions> pagination,
        int? pageSize,
        CancellationToken ct)
    {
        var size = PaginationExtensions.ClampPageSize(pageSize, pagination.Value);

        var items = await queryContext
            .Apply(db.Plants.AsQueryable())
            .OrderBy(p => p.Id)
            .Take(size + 1)
            .ToListAsync(ct).ConfigureAwait(false);

        string? next = null;
        if (items.Count > size)
        {
            next = items[size].Id;
            items = items.Take(size).ToList();
        }

        var dtos = items
            .Select(p => (object)new { p.Id, p.Name, Type = p.Type.ToString(), p.InstalledCapacityMw, p.TimeZone })
            .ToList();

        return Results.Ok(new PagedResult<object>(dtos, next, size));
    }

    // ---- GET /api/v1/plants/{plantId} ------------------------------------

    private static async Task<IResult> GetByIdAsync(
        string plantId,
        KraftverkDbContext db,
        IQueryContext queryContext,
        CancellationToken ct)
    {
        var plant = await queryContext
            .Apply(db.Plants.AsQueryable())
            .FirstOrDefaultAsync(p => p.Id == plantId, ct).ConfigureAwait(false);

        if (plant is null)
        {
            return Results.Problem(
                title: "Anlegg ikke funnet",
                detail: $"Plant '{plantId}' eksisterer ikke.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(new
        {
            plant.Id,
            plant.Name,
            Type = plant.Type.ToString(),
            plant.InstalledCapacityMw,
            plant.TimeZone,
        });
    }

    // ---- PUT /api/v1/plants/{plantId} ------------------------------------

    private static async Task<IResult> UpdateAsync(
        string plantId,
        UpdatePlantRequest body,
        KraftverkDbContext db,
        IQueryContext queryContext,
        CancellationToken ct)
    {
        if (body is null)
        {
            return Results.Problem(
                title: "Manglende body", statusCode: StatusCodes.Status400BadRequest);
        }
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.Problem(
                title: "Ugyldig navn", detail: "Name kan ikke være tomt.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (body.InstalledCapacityMw < 0 || body.InstalledCapacityMw > 1000)
        {
            return Results.Problem(
                title: "Ugyldig kapasitet",
                detail: "InstalledCapacityMw må være mellom 0 og 1000.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (string.IsNullOrWhiteSpace(body.TimeZone))
        {
            return Results.Problem(
                title: "Ugyldig tidssone", statusCode: StatusCodes.Status400BadRequest);
        }
        if (!Enum.TryParse<PlantType>(body.Type, ignoreCase: true, out var typeValue))
        {
            return Results.Problem(
                title: "Ugyldig type",
                detail: $"'{body.Type}' er ikke en gyldig PlantType. Lovlige verdier: Regulated, RunOfRiver, Mixed, Pumped.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var plant = await queryContext
            .Apply(db.Plants.AsQueryable())
            .FirstOrDefaultAsync(p => p.Id == plantId, ct).ConfigureAwait(false);

        if (plant is null)
        {
            return Results.Problem(
                title: "Anlegg ikke funnet",
                detail: $"Plant '{plantId}' eksisterer ikke.",
                statusCode: StatusCodes.Status404NotFound);
        }

        plant.Name = body.Name.Trim();
        plant.Type = typeValue;
        plant.InstalledCapacityMw = body.InstalledCapacityMw;
        plant.TimeZone = body.TimeZone.Trim();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Ok(new
        {
            plant.Id,
            plant.Name,
            Type = plant.Type.ToString(),
            plant.InstalledCapacityMw,
            plant.TimeZone,
        });
    }
}

/// <summary>Request-body for <c>PUT /api/v1/plants/{plantId}</c>.</summary>
public sealed record UpdatePlantRequest(
    string Name,
    string Type,
    double InstalledCapacityMw,
    string TimeZone);
