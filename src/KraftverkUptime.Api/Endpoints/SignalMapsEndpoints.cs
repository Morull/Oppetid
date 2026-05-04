using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Signal-map-administrering: knytter SCADA-tags til roller (eks. OverflowFlow)
/// og spesifikke dammer (kaskade-modell). Brukes av PlantAdmin for å
/// konfigurere overflow-tag på terminal-dam slik at Vakt-ROI kan beregnes.
///
///   GET    /api/v1/plants/{plantId}/signal-maps           — list alle (eller filtrert)
///   POST   /api/v1/plants/{plantId}/signal-maps           — opprett/oppdater tag-mapping
///   DELETE /api/v1/plants/{plantId}/signal-maps/{signalId} — fjern mapping
///   GET    /api/v1/plants/{plantId}/scada-tags            — distinct signal-id-er
///                                                            funnet i SCADA-samples
///                                                            (hjelper brukeren å se
///                                                             hvilke tags som kan mappes)
/// </summary>
public static class SignalMapsEndpoints
{
    public static IEndpointRouteBuilder MapSignalMapsV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var smGroup = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/signal-maps")
            .WithTags("SignalMaps")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        smGroup.MapGet("/", ListAsync)
            .WithName("ListSignalMaps")
            .WithSummary("Lister alle signal-mappinger for et anlegg, evt. filtrert på role/damId.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<SignalMapDto>>(StatusCodes.Status200OK);

        smGroup.MapPost("/", UpsertAsync)
            .WithName("UpsertSignalMap")
            .WithSummary("Opprett eller oppdater mapping for en SCADA-tag (signal_id).")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<SignalMapDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        smGroup.MapDelete("/{signalId}", DeleteAsync)
            .WithName("DeleteSignalMap")
            .WithSummary("Fjern en signal-mapping (deaktiverer tag i analyse).")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var tagsGroup = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/scada-tags")
            .WithTags("SignalMaps")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        tagsGroup.MapGet("/", ListScadaTagsAsync)
            .WithName("ListScadaTags")
            .WithSummary("Lister distinct SCADA-signal-id-er observert i samples — for tag-picker i UI.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<ScadaTagDto>>(StatusCodes.Status200OK);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string plantId,
        SignalRole? role,
        string? damId,
        ISignalMapRepository repo,
        CancellationToken ct)
    {
        var all = await repo.ListForPlantAsync(plantId, ct).ConfigureAwait(false);
        var filtered = all.AsEnumerable();
        if (role.HasValue)
        {
            filtered = filtered.Where(s => s.Role == role.Value);
        }
        if (!string.IsNullOrEmpty(damId))
        {
            filtered = filtered.Where(s => string.Equals(s.DamId, damId, StringComparison.Ordinal));
        }
        return Results.Ok(filtered.Select(ToDto).ToList());
    }

    private static async Task<IResult> UpsertAsync(
        string plantId,
        UpsertSignalMapRequest body,
        ISignalMapRepository repo,
        IDamRepository damRepo,
        IAuditLogger audit,
        KraftverkDbContext db,
        IQueryContext queryContext,
        CancellationToken ct)
    {
        if (body is null
            || string.IsNullOrWhiteSpace(body.SignalId)
            || string.IsNullOrWhiteSpace(body.CsvColumn))
        {
            return Results.Problem(title: "Mangler signalId eller csvColumn",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var plantExists = await queryContext.Apply(db.Plants.AsQueryable())
            .AnyAsync(p => p.Id == plantId, ct).ConfigureAwait(false);
        if (!plantExists)
        {
            return Results.Problem(title: "Anlegg ikke funnet",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Validering: dam-relaterte roller krever damId, og damId må peke på
        // en eksisterende dam for plant-en.
        if (RequiresDamId(body.Role) && string.IsNullOrWhiteSpace(body.DamId))
        {
            return Results.Problem(
                title: "Rolle krever damId",
                detail: $"Rollen '{body.Role}' er dam-relatert og må knyttes til en spesifikk dam.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (!string.IsNullOrEmpty(body.DamId))
        {
            var dams = await damRepo.GetForPlantAsync(plantId, ct).ConfigureAwait(false);
            if (dams.All(d => d.DamId != body.DamId))
            {
                return Results.Problem(
                    title: "Ukjent damId",
                    detail: $"Dam '{body.DamId}' finnes ikke for plant '{plantId}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var signal = new SignalMap(
            PlantId: plantId,
            SignalId: body.SignalId.Trim(),
            CsvColumn: body.CsvColumn.Trim(),
            Unit: body.Unit?.Trim() ?? "",
            Role: body.Role,
            StoreSamples: body.StoreSamples ?? true,
            IsActive: body.IsActive ?? true,
            DamId: string.IsNullOrEmpty(body.DamId) ? null : body.DamId);

        await repo.UpsertAsync(signal, ct).ConfigureAwait(false);

        await audit.LogAsync(
            action: "signal_map.upserted",
            entityType: "SignalMap",
            entityId: $"{plantId}/{signal.SignalId}",
            payload: new { plantId, signal.SignalId, signal.Role, signal.DamId, signal.IsActive },
            ct).ConfigureAwait(false);

        return Results.Ok(ToDto(signal));
    }

    private static async Task<IResult> DeleteAsync(
        string plantId,
        string signalId,
        KraftverkDbContext db,
        IAuditLogger audit,
        CancellationToken ct)
    {
        // Bruker direkte EF-kall siden repo-en ikke har Delete (satt opp for upsert-flow).
        var rows = await db.SignalMaps
            .Where(s => s.PlantId == plantId && s.SignalId == signalId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        if (rows == 0)
        {
            return Results.Problem(title: "Mapping ikke funnet",
                statusCode: StatusCodes.Status404NotFound);
        }
        await audit.LogAsync(
            action: "signal_map.deleted",
            entityType: "SignalMap",
            entityId: $"{plantId}/{signalId}",
            payload: new { plantId, signalId },
            ct).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> ListScadaTagsAsync(
        string plantId,
        KraftverkDbContext db,
        IQueryContext queryContext,
        CancellationToken ct)
    {
        // Distinct signal-id-er observert i sample-tabellen for plant-en.
        // Inkluderer både mappede og ikke-mappede tags så brukeren kan velge
        // fra fullstendig liste i UI. SampleFactEntry bruker AssetId (= plantId).
        var rawTags = await db.SampleFacts.AsNoTracking()
            .Where(s => s.AssetId == plantId)
            .Select(s => s.SignalId)
            .Distinct()
            .OrderBy(s => s)
            .Take(500)
            .ToListAsync(ct).ConfigureAwait(false);

        // Marker hvilke som allerede er mappet
        var mapped = await queryContext.Apply(db.SignalMaps.AsQueryable())
            .Where(s => s.PlantId == plantId)
            .Select(s => new { s.SignalId, s.Role, s.DamId })
            .ToListAsync(ct).ConfigureAwait(false);
        var byId = mapped.ToDictionary(m => m.SignalId, m => m, StringComparer.Ordinal);

        var dtos = rawTags.Select(signalId =>
        {
            byId.TryGetValue(signalId, out var m);
            return new ScadaTagDto(
                SignalId: signalId,
                IsMapped: m is not null,
                Role: m?.Role.ToString(),
                DamId: m?.DamId);
        }).ToList();
        return Results.Ok(dtos);
    }

    private static SignalMapDto ToDto(SignalMap s) => new(
        s.PlantId, s.SignalId, s.CsvColumn, s.Unit, s.Role.ToString(),
        s.StoreSamples, s.IsActive, s.DamId);

    /// <summary>
    /// Roller som krever DamId. Synkronisert med hvilke roller backfill-en
    /// i DefaultDamSeeder oppdaterer (OverflowFlow, UpstreamLevel osv.).
    /// </summary>
    private static bool RequiresDamId(SignalRole role) => role switch
    {
        SignalRole.OverflowFlow => true,
        SignalRole.UpstreamLevel => true,
        SignalRole.DownstreamLevel => true,
        SignalRole.ReservoirFillFactor => true,
        SignalRole.LowestRegulatedLevel => true,
        SignalRole.GateFlow => true,
        SignalRole.GatePosition => true,
        SignalRole.TotalDamFlow => true,
        SignalRole.ReservoirVolume => true,
        _ => false,
    };
}

public sealed record SignalMapDto(
    string PlantId,
    string SignalId,
    string CsvColumn,
    string Unit,
    string Role,
    bool StoreSamples,
    bool IsActive,
    string? DamId);

public sealed record UpsertSignalMapRequest(
    string SignalId,
    string CsvColumn,
    string? Unit,
    SignalRole Role,
    string? DamId,
    bool? StoreSamples,
    bool? IsActive);

public sealed record ScadaTagDto(
    string SignalId,
    bool IsMapped,
    string? Role,
    string? DamId);
