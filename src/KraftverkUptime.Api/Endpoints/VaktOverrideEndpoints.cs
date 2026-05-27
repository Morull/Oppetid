using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// CRUD for manuelle overrides på vakt-events. Identifikasjon: (plantId,
/// eventStartUtc). Brukes når SCADA-overflow-data viser feilaktig overløp
/// (sensor-glitch) eller mangler data der drifts-leder vet at det var/ikke
/// var overløp.
///
/// En override-rad kan bære opptil tre uavhengige overstyringer:
/// <list type="bullet">
///   <item>Classification: <c>Auto</c> (default), <c>HaddeOverlop</c>, <c>IkkeOverlop</c>.</item>
///   <item>ActualEndOverrideUtc: manuell slutt-tid hvis SCADA-data er feil (null = ingen overstyring).</item>
///   <item>GuardResponseOverride (2026-05-22): <c>Auto</c> / <c>Yes</c> / <c>No</c> — om vakta
///     rykket ut. <c>null</c> i request = ikke endre denne overstyringen.</item>
/// </list>
///
/// Raden slettes automatisk når ALLE tre overstyringer er på default
/// (Classification=Auto, ActualEndOverrideUtc=null, GuardResponseOverride=Auto).
///
/// API:
///   GET    /api/v1/plants/{plantId}/vakt-overrides
///   PUT    /api/v1/plants/{plantId}/vakt-overrides   (body: { eventStartUtc, classification, comment?, actualEndOverrideUtc?, guardResponseOverride? })
///   DELETE /api/v1/plants/{plantId}/vakt-overrides?eventStartUtc=...
/// </summary>
public static class VaktOverrideEndpoints
{
    public static IEndpointRouteBuilder MapVaktOverridesV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/vakt-overrides")
            .WithTags("VaktOverrides")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/", ListAsync)
            .WithName("ListVaktOverrides")
            .WithSummary("Lister alle vakt-event-overrides for et anlegg.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<VaktOverrideDto>>(StatusCodes.Status200OK);

        group.MapPut("/", UpsertAsync)
            .WithName("UpsertVaktOverride")
            .WithSummary("Setter eller oppdaterer override for én vakt-event.")
            .RequireAuthorization(AuthorizationPolicies.PlantAnalyst)
            .Produces<VaktOverrideDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapDelete("/", DeleteAsync)
            .WithName("DeleteVaktOverride")
            .WithSummary("Fjerner override (= går tilbake til Auto-klassifisering).")
            .RequireAuthorization(AuthorizationPolicies.PlantAnalyst)
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string plantId, KraftverkDbContext db, CancellationToken ct)
    {
        var rows = await db.VaktEventOverrides
            .Where(o => o.PlantId == plantId)
            .OrderBy(o => o.EventStartUtc)
            .Select(o => new VaktOverrideDto(
                o.PlantId, o.EventStartUtc, o.Classification, o.Comment, o.SetAt, o.SetBy,
                o.ActualEndOverrideUtc, o.GuardResponseOverride))
            .ToListAsync(ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> UpsertAsync(
        string plantId,
        UpsertVaktOverrideRequest body,
        KraftverkDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plantId))
            return Results.Problem(title: "Manglende plantId", statusCode: 400);
        if (body.Classification != "Auto" && body.Classification != "HaddeOverlop" && body.Classification != "IkkeOverlop")
            return Results.Problem(
                title: "Ugyldig classification",
                detail: "Må være 'Auto', 'HaddeOverlop' eller 'IkkeOverlop'.",
                statusCode: 400);

        // Validering: varighetsoverstyring må være etter EventStartUtc.
        if (body.ActualEndOverrideUtc is { } actualEnd && actualEnd <= body.EventStartUtc)
        {
            return Results.Problem(
                title: "Ugyldig actualEndOverrideUtc",
                detail: "Faktisk slutt må være etter hendelsens start-tidspunkt.",
                statusCode: 400);
        }

        var now = DateTimeOffset.UtcNow;
        var existing = await db.VaktEventOverrides
            .FirstOrDefaultAsync(o => o.PlantId == plantId && o.EventStartUtc == body.EventStartUtc, ct)
            .ConfigureAwait(false);

        // En override-rad kan nå bære klassifisering, varighet OG/eller vakt-
        // utrykning — alle tre valgfrie. Slett raden kun hvis ALLE tre er på
        // default (classification = "Auto", ActualEndOverrideUtc = null,
        // GuardResponseOverride = Auto). null i body.GuardResponseOverride betyr
        // "ikke endre", så vi må kombinere med eksisterende verdi for å vurdere
        // den endelige tilstanden.
        var finalGuardResponse = body.GuardResponseOverride
            ?? existing?.GuardResponseOverride
            ?? GuardResponseOverride.Auto;
        var skalSlettes = body.Classification == "Auto"
            && body.ActualEndOverrideUtc is null
            && finalGuardResponse == GuardResponseOverride.Auto;
        if (skalSlettes)
        {
            if (existing is not null)
            {
                db.VaktEventOverrides.Remove(existing);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            return Results.Ok(new VaktOverrideDto(
                plantId, body.EventStartUtc, "Auto", null, now, null, null, GuardResponseOverride.Auto));
        }

        if (existing is null)
        {
            db.VaktEventOverrides.Add(new VaktEventOverrideEntry
            {
                PlantId = plantId,
                EventStartUtc = body.EventStartUtc,
                Classification = body.Classification,
                Comment = body.Comment,
                OwnerOrgId = "dev-org",
                SetAt = now,
                ActualEndOverrideUtc = body.ActualEndOverrideUtc,
                GuardResponseOverride = body.GuardResponseOverride ?? GuardResponseOverride.Auto,
            });
        }
        else
        {
            existing.Classification = body.Classification;
            existing.Comment = body.Comment;
            existing.SetAt = now;
            existing.ActualEndOverrideUtc = body.ActualEndOverrideUtc;
            // null = "ikke endre" — la eksisterende vakt-utrykning-overstyring stå.
            if (body.GuardResponseOverride.HasValue)
            {
                existing.GuardResponseOverride = body.GuardResponseOverride.Value;
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Ok(new VaktOverrideDto(
            plantId, body.EventStartUtc, body.Classification, body.Comment, now, null,
            body.ActualEndOverrideUtc, finalGuardResponse));
    }

    private static async Task<IResult> DeleteAsync(
        string plantId, DateTimeOffset eventStartUtc, KraftverkDbContext db, CancellationToken ct)
    {
        var existing = await db.VaktEventOverrides
            .FirstOrDefaultAsync(o => o.PlantId == plantId && o.EventStartUtc == eventStartUtc, ct)
            .ConfigureAwait(false);
        if (existing is null) return Results.NoContent();
        db.VaktEventOverrides.Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.NoContent();
    }
}

public sealed record VaktOverrideDto(
    string PlantId,
    DateTimeOffset EventStartUtc,
    string Classification,
    string? Comment,
    DateTimeOffset SetAt,
    string? SetBy,
    DateTimeOffset? ActualEndOverrideUtc,
    GuardResponseOverride GuardResponseOverride);

public sealed record UpsertVaktOverrideRequest(
    DateTimeOffset EventStartUtc,
    string Classification,
    string? Comment,
    DateTimeOffset? ActualEndOverrideUtc = null,
    GuardResponseOverride? GuardResponseOverride = null);
