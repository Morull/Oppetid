using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Api.Contracts;
using KraftverkUptime.Api.Options;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Modules.Annotations.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// CRUD for nedetidsannoteringer + lookup på kategorier.
///
/// Auth-modell (V1: alle policies returnerer "allow", men endepunktene er korrekt
/// tagget slik at Entra ID-kobling i V2 fungerer uten endringer her):
/// <list type="bullet">
///   <item>GET annotations + categories → <c>PlantReader</c></item>
///   <item>POST/PATCH/DELETE annotations → <c>PlantAnalyst</c> (drifts-leder kan
///         annotere uten å være admin)</item>
///   <item>POST/PUT/DELETE categories → <c>PlantAdmin</c> (kategori-skjema
///         påvirker hele org)</item>
/// </list>
///
/// Tidsoppløsning: hele timer (UTC). Endepunktene avviser body med ikke-time-justerte
/// tidspunkter med 400.
///
/// Overlapp: en POST/PATCH som gir overlapp uten <c>ReplaceIds</c> som dekker alle
/// kolliderende rader returnerer 409 med listen i body.
/// </summary>
public static class AnnotationsEndpoints
{
    public static IEndpointRouteBuilder MapAnnotationsV1(this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var plantGroup = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/annotations")
            .WithTags("Annotations")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        plantGroup.MapGet("/", ListAsync)
            .WithName("ListAnnotations")
            .WithSummary("Lister aktive annoteringer for et anlegg som overlapper [from, to).")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<DowntimeAnnotationDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        plantGroup.MapPost("/", CreateAsync)
            .WithName("CreateAnnotation")
            .WithSummary("Oppretter ny annotering. Avviser ulovlig overlapp med 409.")
            .RequireAuthorization(AuthorizationPolicies.PlantAnalyst)
            .Produces<DowntimeAnnotationDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        plantGroup.MapPatch("/{id:long}", UpdateAsync)
            .WithName("UpdateAnnotation")
            .WithSummary("Delvis oppdatering av eksisterende annotering.")
            .RequireAuthorization(AuthorizationPolicies.PlantAnalyst)
            .Produces<DowntimeAnnotationDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        plantGroup.MapDelete("/{id:long}", DeleteAsync)
            .WithName("DeleteAnnotation")
            .WithSummary("Soft-delete av annotering.")
            .RequireAuthorization(AuthorizationPolicies.PlantAnalyst)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        var rootGroup = endpoints.MapGroup("/api/v{version:apiVersion}/annotations")
            .WithTags("Annotations")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        rootGroup.MapGet("/categories", ListCategoriesAsync)
            .WithName("ListAnnotationCategories")
            .WithSummary("Lister alle aktive nedetidskategorier (system + bruker).")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<DowntimeCategoryDto>>(StatusCodes.Status200OK);

        rootGroup.MapGet("/categories/all", ListAllCategoriesAsync)
            .WithName("ListAllAnnotationCategories")
            .WithSummary("Lister alle nedetidskategorier inkl. inaktive — for admin-skjermen.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<DowntimeCategoryDto>>(StatusCodes.Status200OK);

        rootGroup.MapPost("/categories", CreateCategoryAsync)
            .WithName("CreateAnnotationCategory")
            .WithSummary("Oppretter en ny brukerdefinert kategori.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<DowntimeCategoryDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        rootGroup.MapPut("/categories/{id}", UpdateCategoryAsync)
            .WithName("UpdateAnnotationCategory")
            .WithSummary("Oppdaterer en eksisterende kategori.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<DowntimeCategoryDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        rootGroup.MapDelete("/categories/{id}", DeleteCategoryAsync)
            .WithName("DeleteAnnotationCategory")
            .WithSummary("Sletter en brukerdefinert kategori (system-kategorier kan ikke slettes).")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    // ---- Handlers --------------------------------------------------------

    private static async Task<IResult> ListAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        IDowntimeAnnotationRepository repo,
        CancellationToken ct)
    {
        if (!from.HasValue || !to.HasValue)
        {
            return Results.Problem(
                title: "Manglende tidsfilter",
                detail: "'from' og 'to' er påkrevd som ISO-8601 UTC.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (from.Value >= to.Value)
        {
            return Results.Problem(
                title: "Ugyldig tidsfilter",
                detail: "'from' må være før 'to'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var rows = await repo.ListAsync(plantId, from.Value, to.Value, ct).ConfigureAwait(false);
        var dtos = rows.Select(DowntimeAnnotationDto.From).ToList();
        return Results.Ok(dtos);
    }

    private static async Task<IResult> CreateAsync(
        string plantId,
        CreateAnnotationRequest request,
        IDowntimeAnnotationRepository repo,
        IDowntimeCategoryRepository categoryRepo,
        ICurrentUser currentUser,
        IAuditLogger audit,
        IOptions<SettlementUploadOptions> uploadOptions,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Results.Problem(title: "Manglende body", statusCode: StatusCodes.Status400BadRequest);
        }
        var validation = ValidateTimes(request.StartUtc, request.EndUtc);
        if (validation is not null)
        {
            return validation;
        }
        var category = await categoryRepo.GetAsync(request.CategoryId, ct).ConfigureAwait(false);
        if (category is null || !category.IsActive)
        {
            return Results.Problem(
                title: "Ukjent kategori",
                detail: $"CategoryId '{request.CategoryId}' finnes ikke eller er deaktivert.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var resolution = await ResolveOverlapAsync(
            repo, plantId, request.StartUtc, request.EndUtc, excludeId: null, request.ReplaceIds, currentUser.UserId, ct);
        if (resolution.ConflictResult is not null)
        {
            return resolution.ConflictResult;
        }

        var ownerOrgId = ResolveOwnerOrgId(currentUser, uploadOptions.Value);
        var now = DateTimeOffset.UtcNow;
        var domain = new DowntimeAnnotation(
            Id: 0,
            OwnerOrgId: ownerOrgId,
            PlantId: plantId,
            StartUtc: request.StartUtc,
            EndUtc: request.EndUtc,
            CategoryId: request.CategoryId,
            Comment: string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment,
            CreatedAt: now,
            CreatedBy: currentUser.UserId,
            UpdatedAt: now,
            UpdatedBy: currentUser.UserId,
            DeletedAt: null,
            DeletedBy: null);

        var id = await repo.CreateAsync(domain, ct).ConfigureAwait(false);
        var saved = await repo.GetAsync(id, ct).ConfigureAwait(false);

        await audit.LogAsync(
            action: "annotation.created",
            entityType: "DowntimeAnnotation",
            entityId: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            payload: new
            {
                plantId,
                request.StartUtc,
                request.EndUtc,
                request.CategoryId,
                ReplacedIdCount = (request.ReplaceIds?.Count) ?? 0
            },
            ct).ConfigureAwait(false);

        return Results.Created($"/api/v1/plants/{plantId}/annotations/{id}",
            DowntimeAnnotationDto.From(saved!));
    }

    private static async Task<IResult> UpdateAsync(
        string plantId,
        long id,
        UpdateAnnotationRequest request,
        IDowntimeAnnotationRepository repo,
        IDowntimeCategoryRepository categoryRepo,
        ICurrentUser currentUser,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Results.Problem(title: "Manglende body", statusCode: StatusCodes.Status400BadRequest);
        }

        var existing = await repo.GetAsync(id, ct).ConfigureAwait(false);
        if (existing is null || existing.PlantId != plantId)
        {
            return Results.Problem(title: "Annotering ikke funnet", statusCode: StatusCodes.Status404NotFound);
        }

        var newStart = request.StartUtc ?? existing.StartUtc;
        var newEnd = request.EndUtc ?? existing.EndUtc;
        var validation = ValidateTimes(newStart, newEnd);
        if (validation is not null)
        {
            return validation;
        }

        if (request.CategoryId is not null)
        {
            var category = await categoryRepo.GetAsync(request.CategoryId, ct).ConfigureAwait(false);
            if (category is null || !category.IsActive)
            {
                return Results.Problem(
                    title: "Ukjent kategori",
                    detail: $"CategoryId '{request.CategoryId}' finnes ikke eller er deaktivert.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        // Bare valider overlapp hvis tidsrom faktisk endres.
        if (request.StartUtc.HasValue || request.EndUtc.HasValue)
        {
            var resolution = await ResolveOverlapAsync(
                repo, plantId, newStart, newEnd, excludeId: id, request.ReplaceIds, currentUser.UserId, ct);
            if (resolution.ConflictResult is not null)
            {
                return resolution.ConflictResult;
            }
        }

        var updated = await repo.UpdateAsync(
            id,
            request.StartUtc,
            request.EndUtc,
            request.CategoryId,
            request.Comment,
            currentUser.UserId,
            ct).ConfigureAwait(false);

        if (updated is null)
        {
            return Results.Problem(title: "Annotering ikke funnet", statusCode: StatusCodes.Status404NotFound);
        }

        await audit.LogAsync(
            action: "annotation.updated",
            entityType: "DowntimeAnnotation",
            entityId: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            payload: new
            {
                plantId,
                Before = new { existing.StartUtc, existing.EndUtc, existing.CategoryId, existing.Comment },
                After = new { updated.StartUtc, updated.EndUtc, updated.CategoryId, updated.Comment }
            },
            ct).ConfigureAwait(false);

        return Results.Ok(DowntimeAnnotationDto.From(updated));
    }

    private static async Task<IResult> DeleteAsync(
        string plantId,
        long id,
        IDowntimeAnnotationRepository repo,
        ICurrentUser currentUser,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var existing = await repo.GetAsync(id, ct).ConfigureAwait(false);
        if (existing is null || existing.PlantId != plantId)
        {
            return Results.Problem(title: "Annotering ikke funnet", statusCode: StatusCodes.Status404NotFound);
        }

        await repo.SoftDeleteAsync(id, currentUser.UserId, ct).ConfigureAwait(false);

        await audit.LogAsync(
            action: "annotation.deleted",
            entityType: "DowntimeAnnotation",
            entityId: id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            payload: new
            {
                plantId,
                existing.StartUtc,
                existing.EndUtc,
                existing.CategoryId,
                existing.Comment
            },
            ct).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> ListCategoriesAsync(
        IDowntimeCategoryRepository repo,
        CancellationToken ct)
    {
        var rows = await repo.ListActiveAsync(ct).ConfigureAwait(false);
        var dtos = rows.Select(DowntimeCategoryDto.From).ToList();
        return Results.Ok(dtos);
    }

    private static async Task<IResult> ListAllCategoriesAsync(
        IDowntimeCategoryRepository repo,
        CancellationToken ct)
    {
        var rows = await repo.ListAllAsync(ct).ConfigureAwait(false);
        var dtos = rows.Select(DowntimeCategoryDto.From).ToList();
        return Results.Ok(dtos);
    }

    private static async Task<IResult> CreateCategoryAsync(
        CreateCategoryRequest request,
        IDowntimeCategoryRepository repo,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Results.Problem(title: "Mangler body",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            return Results.Problem(title: "Id er påkrevd",
                detail: "Bruk en stabil slug, f.eks. 'rist_blokkering'.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName))
        {
            return Results.Problem(title: "DisplayName er påkrevd",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (await repo.GetAsync(request.Id, ct).ConfigureAwait(false) is not null)
        {
            return Results.Problem(title: "Kategori finnes allerede",
                detail: $"Id '{request.Id}' er allerede i bruk.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var category = new DowntimeCategory(
            Id: request.Id.Trim(),
            DisplayName: request.DisplayName.Trim(),
            ColorHex: string.IsNullOrWhiteSpace(request.ColorHex) ? "#888888" : request.ColorHex.Trim(),
            UnitStateOverride: request.UnitStateOverride,
            SortOrder: request.SortOrder,
            IsActive: request.IsActive,
            IsSystem: false,
            Description: string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim());
        await repo.AddAsync(category, ct).ConfigureAwait(false);

        await audit.LogAsync(
            action: "annotation_category.created",
            entityType: "DowntimeCategory",
            entityId: category.Id,
            payload: new
            {
                category.Id,
                category.DisplayName,
                category.ColorHex,
                category.UnitStateOverride,
                category.SortOrder,
                category.IsActive
            },
            ct).ConfigureAwait(false);

        return Results.Created(
            $"/api/v1/annotations/categories/{request.Id}",
            DowntimeCategoryDto.From(category));
    }

    private static async Task<IResult> UpdateCategoryAsync(
        string id,
        UpdateCategoryRequest request,
        IDowntimeCategoryRepository repo,
        IAuditLogger audit,
        CancellationToken ct)
    {
        if (request is null)
        {
            return Results.Problem(title: "Mangler body",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var existing = await repo.GetAsync(id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return Results.Problem(title: "Kategori ikke funnet",
                statusCode: StatusCodes.Status404NotFound);
        }

        var updated = existing with
        {
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? existing.DisplayName : request.DisplayName.Trim(),
            ColorHex = string.IsNullOrWhiteSpace(request.ColorHex) ? existing.ColorHex : request.ColorHex.Trim(),
            UnitStateOverride = request.UnitStateOverride ?? existing.UnitStateOverride,
            SortOrder = request.SortOrder ?? existing.SortOrder,
            IsActive = request.IsActive ?? existing.IsActive,
            // Description: tom string tolkes som "fjern" (set null); null i payload
            // betyr "ikke endre". Null check via Description == null vs != null.
            Description = request.Description is null ? existing.Description
                          : (string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()),
        };
        await repo.UpdateAsync(updated, ct).ConfigureAwait(false);

        await audit.LogAsync(
            action: "annotation_category.updated",
            entityType: "DowntimeCategory",
            entityId: id,
            payload: new
            {
                Before = new
                {
                    existing.DisplayName, existing.ColorHex, existing.UnitStateOverride,
                    existing.SortOrder, existing.IsActive, existing.Description
                },
                After = new
                {
                    updated.DisplayName, updated.ColorHex, updated.UnitStateOverride,
                    updated.SortOrder, updated.IsActive, updated.Description
                }
            },
            ct).ConfigureAwait(false);

        return Results.Ok(DowntimeCategoryDto.From(updated));
    }

    private static async Task<IResult> DeleteCategoryAsync(
        string id,
        IDowntimeCategoryRepository repo,
        IAuditLogger audit,
        CancellationToken ct)
    {
        try
        {
            await repo.DeleteAsync(id, ct).ConfigureAwait(false);

            await audit.LogAsync(
                action: "annotation_category.deleted",
                entityType: "DowntimeCategory",
                entityId: id,
                payload: null,
                ct).ConfigureAwait(false);

            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Kan ikke slette",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    public sealed record CreateCategoryRequest(
        string Id,
        string DisplayName,
        string? ColorHex,
        UnitState UnitStateOverride,
        int SortOrder,
        bool IsActive,
        string? Description);

    public sealed record UpdateCategoryRequest(
        string? DisplayName,
        string? ColorHex,
        UnitState? UnitStateOverride,
        int? SortOrder,
        bool? IsActive,
        string? Description);

    // ---- Helpers ---------------------------------------------------------

    private static IResult? ValidateTimes(DateTimeOffset start, DateTimeOffset end)
    {
        if (start >= end)
        {
            return Results.Problem(
                title: "Ugyldig tidsrom",
                detail: "StartUtc må være før EndUtc.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        if (!IsHourAligned(start) || !IsHourAligned(end))
        {
            return Results.Problem(
                title: "Tidsrom må være time-justert",
                detail: "Annoteringer har time-presisjon. Minutter, sekunder og millisekunder må være 0.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        return null;
    }

    private static bool IsHourAligned(DateTimeOffset value) =>
        value.Minute == 0 && value.Second == 0 && value.Millisecond == 0 && value.Ticks % TimeSpan.TicksPerSecond == 0;

    private static async Task<OverlapResolution> ResolveOverlapAsync(
        IDowntimeAnnotationRepository repo,
        string plantId,
        DateTimeOffset start,
        DateTimeOffset end,
        long? excludeId,
        IReadOnlyList<long>? replaceIds,
        string? deletedBy,
        CancellationToken ct)
    {
        var overlapping = await repo.FindOverlappingAsync(plantId, start, end, excludeId, ct).ConfigureAwait(false);
        if (overlapping.Count == 0)
        {
            return new OverlapResolution(null);
        }

        var replaceSet = (replaceIds ?? Array.Empty<long>()).ToHashSet();
        var unresolved = overlapping.Where(o => !replaceSet.Contains(o.Id)).ToList();
        if (unresolved.Count > 0)
        {
            var conflict = new AnnotationOverlapConflict(
                Message: "Tidsrommet overlapper eksisterende annoteringer. Send 'replaceIds' som dekker alle for å erstatte dem.",
                Overlapping: unresolved.Select(DowntimeAnnotationDto.From).ToList());
            return new OverlapResolution(Results.Json(conflict, statusCode: StatusCodes.Status409Conflict));
        }

        // Soft-delete alle eksplisitt erstattede.
        foreach (var idToReplace in replaceSet)
        {
            await repo.SoftDeleteAsync(idToReplace, deletedBy, ct).ConfigureAwait(false);
        }
        return new OverlapResolution(null);
    }

    private static string ResolveOwnerOrgId(ICurrentUser currentUser, SettlementUploadOptions opts)
    {
        if (!string.Equals(currentUser.OrgId, "system", StringComparison.Ordinal))
        {
            return currentUser.OrgId;
        }
        if (string.IsNullOrWhiteSpace(opts.DevDefaultOwnerOrgId))
        {
            throw new InvalidOperationException(
                "Settlements:DevDefaultOwnerOrgId må være satt når anonym authn er aktiv.");
        }
        return opts.DevDefaultOwnerOrgId;
    }

    private readonly record struct OverlapResolution(IResult? ConflictResult);
}
