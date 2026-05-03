using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Dam-administrering for kaskade-modellen (Spec KASKADE-DAMMER):
///   GET /api/v1/plants/{plantId}/dams         — list alle dammer for et anlegg
///   PUT /api/v1/plants/{plantId}/dams/{damId} — oppdater HRV/LRV/Volum/intake
///
/// V1 ikke støttet: POST (ny dam) og DELETE — backfill garanterer at hvert
/// plant har minst én default-dam, og IsTurbineIntake kan flyttes mellom
/// eksisterende dammer. Multi-dam-anlegg (Haukland) seedes via dedikert
/// kode-seeder (HauklandSignalMapSeeder) for nå.
/// </summary>
public static class DamsEndpoints
{
    public static IEndpointRouteBuilder MapDamsV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/dams")
            .WithTags("Dams")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/", ListAsync)
            .WithName("ListDams")
            .WithSummary("Lister dammene for et anlegg, sortert etter cascade_position.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<DamDto>>(StatusCodes.Status200OK);

        group.MapPut("/{damId}", UpdateAsync)
            .WithName("UpdateDam")
            .WithSummary("Oppdater HRV/LRV/Volum/IsTurbineIntake for en eksisterende dam.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<DamDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string plantId,
        IDamRepository dams,
        KraftverkDbContext db,
        IQueryContext queryContext,
        CancellationToken ct)
    {
        var plantExists = await queryContext.Apply(db.Plants.AsQueryable())
            .AnyAsync(p => p.Id == plantId, ct).ConfigureAwait(false);
        if (!plantExists)
        {
            return Results.Problem(
                title: "Anlegg ikke funnet",
                statusCode: StatusCodes.Status404NotFound);
        }

        var rows = await dams.GetForPlantAsync(plantId, ct).ConfigureAwait(false);
        return Results.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<IResult> UpdateAsync(
        string plantId,
        string damId,
        UpdateDamRequest body,
        IDamRepository dams,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (body is null)
        {
            return Results.Problem(title: "Mangler body",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Constraint-håndtering: hvis bruker setter IsTurbineIntake=true,
        // må vi først fjerne markøren fra alle andre dammer i samme plant.
        // Constraint er deferrable så det skjer i én transaksjon i Postgres.
        var existing = await dams.GetForPlantAsync(plantId, ct).ConfigureAwait(false);
        var target = existing.FirstOrDefault(d => d.DamId == damId);
        if (target is null)
        {
            return Results.Problem(
                title: "Dam ikke funnet",
                detail: $"Dam '{damId}' finnes ikke for plant '{plantId}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Hvis vi setter denne til intake → fjern intake fra de andre først.
        if (body.IsTurbineIntake && !target.IsTurbineIntake)
        {
            foreach (var other in existing.Where(d => d.IsTurbineIntake && d.DamId != damId))
            {
                await dams.UpdateAsync(other with { IsTurbineIntake = false }, ct)
                    .ConfigureAwait(false);
            }
        }
        // Validering: minst én må være terminal etter update.
        if (!body.IsTurbineIntake && target.IsTurbineIntake
            && !existing.Any(d => d.DamId != damId && d.IsTurbineIntake))
        {
            return Results.Problem(
                title: "Minst én dam må være terminal",
                detail: "Du kan ikke fjerne IsTurbineIntake fra denne dammen uten å samtidig sette den på en annen.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var updated = target with
        {
            Name = string.IsNullOrWhiteSpace(body.Name) ? target.Name : body.Name,
            CascadePosition = body.CascadePosition ?? target.CascadePosition,
            IsTurbineIntake = body.IsTurbineIntake,
            HrvMoh = body.HrvMoh,
            LrvMoh = body.LrvMoh,
            VolumeMm3 = body.VolumeMm3,
        };
        await dams.UpdateAsync(updated, ct).ConfigureAwait(false);

        await audit.LogAsync(
            action: "dam.updated",
            entityType: "Dam",
            entityId: $"{plantId}/{damId}",
            payload: new
            {
                plantId,
                damId,
                Before = new
                {
                    target.Name, target.CascadePosition, target.IsTurbineIntake,
                    target.HrvMoh, target.LrvMoh, target.VolumeMm3
                },
                After = new
                {
                    updated.Name, updated.CascadePosition, updated.IsTurbineIntake,
                    updated.HrvMoh, updated.LrvMoh, updated.VolumeMm3
                }
            },
            ct).ConfigureAwait(false);

        return Results.Ok(ToDto(updated));
    }

    private static DamDto ToDto(Dam d) => new(
        d.PlantId, d.DamId, d.Name, d.CascadePosition, d.IsTurbineIntake,
        d.HrvMoh, d.LrvMoh, d.VolumeMm3);
}

public sealed record DamDto(
    string PlantId,
    string DamId,
    string Name,
    int CascadePosition,
    bool IsTurbineIntake,
    double? HrvMoh,
    double? LrvMoh,
    double? VolumeMm3);

public sealed record UpdateDamRequest(
    string? Name,
    int? CascadePosition,
    bool IsTurbineIntake,
    double? HrvMoh,
    double? LrvMoh,
    double? VolumeMm3);
