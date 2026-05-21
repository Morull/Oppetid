using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Api.Options;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Modules.Scada.Import;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Multi-anleggs SCADA master-CSV-import:
///   POST /api/v1/scada/multi-plant
///
/// Tar imot én master-CSV som inneholder tags fra flere anlegg (eks. samlet
/// eksport for Vikeså, Stølskraft, Ørsdalen, Øgreyfoss, Løgjen) og splitter
/// per signal-prefiks. Hver tag rutes til riktig anlegg via prefix-map som
/// ligger statisk i <see cref="Infrastructure.Scada.ScadaImportService"/>.
/// Resultatet blir én <c>data_imports</c>-rad per anlegg som har tags i fila.
/// </summary>
public static class MultiPlantScadaEndpoints
{
    public static IEndpointRouteBuilder MapMultiPlantScadaV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/scada")
            .WithTags("Scada")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapPost("/multi-plant", UploadMultiPlantMasterAsync)
            .WithName("UploadMultiPlantScadaMaster")
            .WithSummary("Tar imot én master-CSV med tags fra flere anlegg, splitter per signal-prefiks.")
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .AddEndpointFilter(async (ctx, next) =>
            {
                var feature = ctx.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is not null && !feature.IsReadOnly)
                {
                    feature.MaxRequestBodySize = 100L * 1024 * 1024;
                }
                return await next(ctx).ConfigureAwait(false);
            })
            .Produces<MultiPlantScadaImportResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        // 15-min-variant: skriver til sample_facts_fine i stedet for sample_facts.
        // Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md.
        group.MapPost("/multi-plant-fine", UploadMultiPlantMasterFineAsync)
            .WithName("UploadMultiPlantScadaMasterFine")
            .WithSummary("Tar imot én 15-min master-CSV med tags fra flere anlegg — sample_facts_fine.")
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .AddEndpointFilter(async (ctx, next) =>
            {
                // 15-min er ~4× radmengde sammenlignet med hourly — gi 200 MB rom.
                var feature = ctx.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is not null && !feature.IsReadOnly)
                {
                    feature.MaxRequestBodySize = 200L * 1024 * 1024;
                }
                return await next(ctx).ConfigureAwait(false);
            })
            .Produces<MultiPlantScadaImportResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        return endpoints;
    }

    private static Task<IResult> UploadMultiPlantMasterFineAsync(
        HttpRequest request,
        IScadaImportService import,
        ICurrentUser currentUser,
        IAuditLogger audit,
        IOptions<SettlementUploadOptions> uploadOptions,
        CancellationToken ct)
        => UploadMultiPlantMasterInternalAsync(
            request, import, currentUser, audit, uploadOptions, isFine: true, ct);

    private static Task<IResult> UploadMultiPlantMasterAsync(
        HttpRequest request,
        IScadaImportService import,
        ICurrentUser currentUser,
        IAuditLogger audit,
        IOptions<SettlementUploadOptions> uploadOptions,
        CancellationToken ct)
        => UploadMultiPlantMasterInternalAsync(
            request, import, currentUser, audit, uploadOptions, isFine: false, ct);

    private static async Task<IResult> UploadMultiPlantMasterInternalAsync(
        HttpRequest request,
        IScadaImportService import,
        ICurrentUser currentUser,
        IAuditLogger audit,
        IOptions<SettlementUploadOptions> uploadOptions,
        bool isFine,
        CancellationToken ct)
    {
        if (!IsMultipart(request, out var boundary, out var problem))
        {
            return problem!;
        }

        var ownerOrgId = ResolveOwnerOrgId(currentUser, uploadOptions.Value);

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
                    var result = isFine
                        ? await import.ImportMasterCsvMultiPlantFineAsync(
                            ownerOrgId, section.Body, ct).ConfigureAwait(false)
                        : await import.ImportMasterCsvMultiPlantAsync(
                            ownerOrgId, section.Body, ct).ConfigureAwait(false);

                    await audit.LogAsync(
                        action: isFine ? "scada.multi_plant_master_fine_imported"
                                       : "scada.multi_plant_master_imported",
                        entityType: "ScadaImport",
                        entityId: $"{ownerOrgId}:{DateTimeOffset.UtcNow:o}",
                        payload: new
                        {
                            ownerOrgId,
                            FileName = fileName,
                            IsFine = isFine,
                            result.TotalRowsParsed,
                            result.TotalRowsSkipped,
                            UnknownCount = result.UnknownSignals.Count,
                            PlantCount = result.PerPlant.Count
                        },
                        ct).ConfigureAwait(false);

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
