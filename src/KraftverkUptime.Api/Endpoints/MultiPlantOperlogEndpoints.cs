using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Api.Options;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Scada.Import;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Multi-anleggs operlog-import:
///   POST /api/v1/operlog/multi-plant
///
/// Tar imot én operlog-CSV som dekker hele porteføljen og splitter på
/// <c>station</c>-feltet. Hver event rutes til riktig anlegg via slug-
/// matching mot <c>core.plants</c>. Ukjente stasjoner skippes og rapporteres
/// i responsen.
/// </summary>
public static class MultiPlantOperlogEndpoints
{
    public static IEndpointRouteBuilder MapMultiPlantOperlogV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/operlog")
            .WithTags("Operlog")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapPost("/multi-plant", UploadMultiPlantOperlogAsync)
            .WithName("UploadMultiPlantOperlog")
            .WithSummary("Tar imot én operlog-CSV med events fra alle anlegg, splitter på station-feltet.")
            .DisableAntiforgery()
            .AllowAnonymous() // TODO: PlantAdmin-policy når Entra ID kobles til
            .AddEndpointFilter(async (ctx, next) =>
            {
                // Operlog-CSV kan være 50+ MB for full Q1 (74k+ rader). Hev
                // body-grensen tilsvarende.
                var feature = ctx.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is not null && !feature.IsReadOnly)
                {
                    feature.MaxRequestBodySize = 100L * 1024 * 1024; // 100 MB
                }
                return await next(ctx).ConfigureAwait(false);
            })
            .Produces<MultiPlantOperlogImportResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        return endpoints;
    }

    private static async Task<IResult> UploadMultiPlantOperlogAsync(
        HttpRequest request,
        IScadaImportService import,
        KraftverkDbContext db,
        IQueryContext queryContext,
        ICurrentUser currentUser,
        IOptions<SettlementUploadOptions> uploadOptions,
        CancellationToken ct)
    {
        if (!IsMultipart(request, out var boundary, out var problem))
        {
            return problem!;
        }

        var ownerOrgId = ResolveOwnerOrgId(currentUser, uploadOptions.Value);

        // Bygg station→plantId-lookup ÉN gang før parsing. Bruker
        // PlantSlug.ToSlug på Plant.Name for å matche kanoniske navn med æøå.
        var plants = await queryContext.Apply(db.Plants.AsQueryable())
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        var slugToPlantId = plants.ToDictionary(
            p => PlantSlug.ToSlug(p.Name), p => p.Id,
            StringComparer.Ordinal);
        var idDirect = plants.ToDictionary(p => p.Id, p => p.Id, StringComparer.Ordinal);

        string? StationLookup(string station)
        {
            // 1. Slug-match på navn (Drivdal → drivdal, Grødemfoss → grodemfoss)
            var slug = PlantSlug.ToSlug(station);
            if (slugToPlantId.TryGetValue(slug, out var byName)) return byName;
            // 2. Direkte plant-id-match (hvis station tilfeldigvis er allerede slug)
            if (idDirect.TryGetValue(slug, out var byId)) return byId;
            return null;
        }

        var reader = new MultipartReader(boundary!, request.Body);
        MultipartSection? section;
        try
        {
            section = await reader.ReadNextSectionAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            return Results.Problem(title: "Ugyldig multipart-body",
                detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        while (section is not null)
        {
            if (TryGetFileSection(section, out var fileName))
            {
                if (fileName is not null && !fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Problem(title: "Ikke-støttet filtype",
                        detail: $"Forventer .csv, fikk '{fileName}'.",
                        statusCode: StatusCodes.Status415UnsupportedMediaType);
                }

                try
                {
                    var result = await import.ImportOperlogMultiPlantAsync(
                        ownerOrgId, section.Body, StationLookup, ct).ConfigureAwait(false);
                    return Results.Ok(result);
                }
                catch (InvalidDataException ex)
                {
                    return Results.Problem(title: "Ugyldig CSV",
                        detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return Results.Problem(title: "Filen er for stor",
                        detail: ex.Message, statusCode: StatusCodes.Status413PayloadTooLarge);
                }
            }

            try
            {
                section = await reader.ReadNextSectionAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem(title: "Ugyldig multipart-body",
                    detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }

        return Results.Problem(title: "Mangler fil",
            detail: "Fant ingen fil-seksjon i multipart/form-data.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static string ResolveOwnerOrgId(ICurrentUser currentUser, SettlementUploadOptions opts)
    {
        if (!string.Equals(currentUser.OrgId, "system", StringComparison.Ordinal))
        {
            return currentUser.OrgId;
        }
        return string.IsNullOrWhiteSpace(opts.DevDefaultOwnerOrgId)
            ? throw new InvalidOperationException("DevDefaultOwnerOrgId må være satt for anonym authn.")
            : opts.DevDefaultOwnerOrgId;
    }

    private static bool IsMultipart(HttpRequest request, out string? boundary, out IResult? problem)
    {
        boundary = null;
        problem = null;
        if (!request.HasFormContentType
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            problem = Results.Problem(title: "Forventer multipart/form-data",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
            return false;
        }
        var raw = HeaderUtilities.RemoveQuotes(mediaType.Boundary).ToString();
        if (string.IsNullOrEmpty(raw))
        {
            problem = Results.Problem(title: "Mangler multipart boundary",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
        boundary = raw;
        return true;
    }

    private static bool TryGetFileSection(MultipartSection section, out string? fileName)
    {
        fileName = null;
        if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
            return false;
        if (!disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
            return false;
        var starName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).ToString();
        var plainName = HeaderUtilities.RemoveQuotes(disposition.FileName).ToString();
        var raw = !string.IsNullOrEmpty(starName) ? starName : plainName;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        fileName = raw;
        return true;
    }
}
