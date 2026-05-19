using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Dam-administrering for kaskade-modellen (Spec KASKADE-DAMMER):
///   GET    /api/v1/plants/{plantId}/dams           — list alle dammer for et anlegg
///   POST   /api/v1/plants/{plantId}/dams           — legg til ny kaskade-dam
///   PUT    /api/v1/plants/{plantId}/dams/{damId}   — oppdater HRV/LRV/Volum/intake
///   DELETE /api/v1/plants/{plantId}/dams/{damId}   — slett (krever annen terminal igjen)
///
/// Default: hvert anlegg får én default terminal-dam ved oppstart via
/// <c>DefaultDamSeeder</c>. Drifts-leder kan deretter:
///   - Oppdatere HRV/LRV/Volum på default-dammen
///   - Legge til kaskade-dammer (via POST)
///   - Flytte terminal-markøren (via PUT IsTurbineIntake=true)
///   - Slette kaskade-dammer som ikke lenger er i bruk
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

        group.MapPost("/", CreateAsync)
            .WithName("CreateDam")
            .WithSummary("Legg til ny dam (typisk for kaskade-utvidelse).")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<DamDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{damId}", UpdateAsync)
            .WithName("UpdateDam")
            .WithSummary("Oppdater HRV/LRV/Volum/IsTurbineIntake for en eksisterende dam.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<DamDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapDelete("/{damId}", DeleteAsync)
            .WithName("DeleteDam")
            .WithSummary("Slett en dam. Kan ikke slette siste dam eller terminal uten alternativ.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        string plantId,
        CreateDamRequest body,
        IDamRepository dams,
        IAuditLogger audit,
        KraftverkDbContext db,
        IQueryContext queryContext,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.DamId) || string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.Problem(title: "Mangler damId eller name",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var plantExists = await queryContext.Apply(db.Plants.AsQueryable())
            .AnyAsync(p => p.Id == plantId, ct).ConfigureAwait(false);
        if (!plantExists)
        {
            return Results.Problem(title: "Anlegg ikke funnet",
                statusCode: StatusCodes.Status404NotFound);
        }

        var existing = await dams.GetForPlantAsync(plantId, ct).ConfigureAwait(false);
        if (existing.Any(d => string.Equals(d.DamId, body.DamId, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Problem(
                title: "DamId finnes allerede",
                detail: $"Plant '{plantId}' har allerede en dam med id '{body.DamId}'.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Hvis bruker ber om at den nye damen skal være terminal, fjern markøren
        // fra eksisterende terminal først (deferrable constraint sikrer atomicity).
        if (body.IsTurbineIntake)
        {
            foreach (var prev in existing.Where(d => d.IsTurbineIntake))
            {
                await dams.UpdateAsync(prev with { IsTurbineIntake = false }, ct).ConfigureAwait(false);
            }
        }

        var newDam = new Dam(
            PlantId: plantId,
            DamId: body.DamId.Trim(),
            Name: body.Name.Trim(),
            CascadePosition: body.CascadePosition ?? (existing.Count + 1),
            IsTurbineIntake: body.IsTurbineIntake,
            HrvMoh: body.HrvMoh,
            LrvMoh: body.LrvMoh,
            VolumeMm3: body.VolumeMm3,
            OverflowProxyThresholdCm: body.OverflowProxyThresholdCm);
        await dams.AddAsync(newDam, ct).ConfigureAwait(false);

        await audit.LogAsync(
            action: "dam.created",
            entityType: "Dam",
            entityId: $"{plantId}/{newDam.DamId}",
            payload: new { plantId, newDam.DamId, newDam.Name, newDam.CascadePosition, newDam.IsTurbineIntake },
            ct).ConfigureAwait(false);

        return Results.Created($"/api/v1/plants/{plantId}/dams/{newDam.DamId}", ToDto(newDam));
    }

    private static async Task<IResult> DeleteAsync(
        string plantId,
        string damId,
        IDamRepository dams,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var existing = await dams.GetForPlantAsync(plantId, ct).ConfigureAwait(false);
        var target = existing.FirstOrDefault(d => d.DamId == damId);
        if (target is null)
        {
            return Results.Problem(title: "Dam ikke funnet",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (existing.Count == 1)
        {
            return Results.Problem(
                title: "Kan ikke slette siste dam",
                detail: "Hvert anlegg må ha minst én dam (terminal). Opprett en ny først, eller behold denne.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (target.IsTurbineIntake)
        {
            return Results.Problem(
                title: "Kan ikke slette terminal-dammen",
                detail: "Sett IsTurbineIntake på en annen dam først (PUT), så kan du slette denne.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await dams.DeleteAsync(plantId, damId, ct).ConfigureAwait(false);

        await audit.LogAsync(
            action: "dam.deleted",
            entityType: "Dam",
            entityId: $"{plantId}/{damId}",
            payload: new { plantId, damId, target.Name, target.CascadePosition },
            ct).ConfigureAwait(false);

        return Results.NoContent();
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
            OverflowProxyThresholdCm = body.OverflowProxyThresholdCm,
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
        d.HrvMoh, d.LrvMoh, d.VolumeMm3, d.OverflowProxyThresholdCm);
}

public sealed record DamDto(
    string PlantId,
    string DamId,
    string Name,
    int CascadePosition,
    bool IsTurbineIntake,
    double? HrvMoh,
    double? LrvMoh,
    double? VolumeMm3,
    int? OverflowProxyThresholdCm);

public sealed record UpdateDamRequest(
    string? Name,
    int? CascadePosition,
    bool IsTurbineIntake,
    double? HrvMoh,
    double? LrvMoh,
    double? VolumeMm3,
    int? OverflowProxyThresholdCm);

public sealed record CreateDamRequest(
    string DamId,
    string Name,
    int? CascadePosition,
    bool IsTurbineIntake,
    double? HrvMoh,
    double? LrvMoh,
    double? VolumeMm3,
    int? OverflowProxyThresholdCm);
