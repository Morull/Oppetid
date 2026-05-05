using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// CRUD for cause-aliaser. Lar drifts-leder gi en intern cause-kode (eks.
/// <c>operlog:nodstopp</c>) en brukervennlig visnings-tekst (eks.
/// "Nødstopp utløst") som vises i Nedetid/Rapport/Vakt-ROI-tabellene.
///
/// API:
///   GET    /api/v1/admin/cause-aliases
///   PUT    /api/v1/admin/cause-aliases/{causeCode}
///   DELETE /api/v1/admin/cause-aliases/{causeCode}
/// </summary>
public static class CauseAliasEndpoints
{
    public static IEndpointRouteBuilder MapCauseAliasesV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/cause-aliases")
            .WithTags("CauseAliases")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/", ListAsync)
            .WithName("ListCauseAliases")
            .WithSummary("Lister alle cause-aliaser sortert alfabetisk på cause-kode.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<CauseAliasDto>>(StatusCodes.Status200OK);

        group.MapPut("/{causeCode}", UpsertAsync)
            .WithName("UpsertCauseAlias")
            .WithSummary("Oppretter eller oppdaterer alias for en cause-kode.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<CauseAliasDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapDelete("/{causeCode}", DeleteAsync)
            .WithName("DeleteCauseAlias")
            .WithSummary("Sletter alias for en cause-kode (UI faller tilbake til intern kode).")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        KraftverkDbContext db, CancellationToken ct)
    {
        var rows = await db.CauseAliases
            .OrderBy(a => a.CauseCode)
            .Select(a => new CauseAliasDto(a.CauseCode, a.DisplayText, a.UpdatedAt))
            .ToListAsync(ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> UpsertAsync(
        string causeCode,
        UpsertCauseAliasRequest request,
        KraftverkDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(causeCode))
        {
            return Results.Problem(title: "Ugyldig cause-kode", statusCode: 400);
        }
        if (string.IsNullOrWhiteSpace(request.DisplayText))
        {
            return Results.Problem(title: "DisplayText er påkrevd", statusCode: 400);
        }
        if (request.DisplayText.Length > 200)
        {
            return Results.Problem(title: "DisplayText kan ikke være over 200 tegn", statusCode: 400);
        }

        var now = DateTimeOffset.UtcNow;
        var existing = await db.CauseAliases
            .FirstOrDefaultAsync(a => a.CauseCode == causeCode, ct).ConfigureAwait(false);

        if (existing is null)
        {
            db.CauseAliases.Add(new CauseAliasEntry
            {
                CauseCode = causeCode,
                OwnerOrgId = "dev-org",
                DisplayText = request.DisplayText.Trim(),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        else
        {
            existing.DisplayText = request.DisplayText.Trim();
            existing.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Results.Ok(new CauseAliasDto(causeCode, request.DisplayText.Trim(), now));
    }

    private static async Task<IResult> DeleteAsync(
        string causeCode, KraftverkDbContext db, CancellationToken ct)
    {
        var existing = await db.CauseAliases
            .FirstOrDefaultAsync(a => a.CauseCode == causeCode, ct).ConfigureAwait(false);
        if (existing is null) return Results.NoContent(); // idempotent
        db.CauseAliases.Remove(existing);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Results.NoContent();
    }
}

public sealed record CauseAliasDto(string CauseCode, string DisplayText, DateTimeOffset UpdatedAt);
public sealed record UpsertCauseAliasRequest(string DisplayText);
