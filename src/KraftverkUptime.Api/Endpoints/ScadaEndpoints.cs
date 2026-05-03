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
/// SCADA-import-endepunkter. Tar imot multipart-opplastning av:
///   POST /api/v1/plants/{plantId}/scada           — master-CSV (tidsserier)
///   POST /api/v1/plants/{plantId}/scada/operlog   — operatorlog (events)
///
/// Begge krever <see cref="AuthorizationPolicies.PlantAdmin"/>. V1: policy
/// returnerer "allow" frem til Entra ID kobles til, men taggene er på plass
/// slik at Entra-kobling ikke krever endpoint-endringer.
/// </summary>
public static class ScadaEndpoints
{
    public static IEndpointRouteBuilder MapScadaV1(this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/scada")
            .WithTags("Scada")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapPost("/", UploadMasterAsync)
            .WithName("UploadScadaMaster")
            .WithSummary("Laster opp SCADA master-CSV (tidsserier).")
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .AddEndpointFilter(EnsureMaxBodySize)
            .Produces<ScadaImportResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        group.MapPost("/operlog", UploadOperlogAsync)
            .WithName("UploadScadaOperlog")
            .WithSummary("Laster opp SCADA operatorlog (events).")
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .AddEndpointFilter(EnsureMaxBodySize)
            .Produces<OperlogImportResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    // ---- Handlers --------------------------------------------------------

    private static Task<IResult> UploadMasterAsync(
        string plantId,
        HttpRequest request,
        IScadaImportService import,
        ICurrentUser currentUser,
        IAuditLogger audit,
        IOptions<SettlementUploadOptions> uploadOptions,
        CancellationToken ct) =>
        ProcessUploadAsync(plantId, request, async (stream, ownerOrgId) =>
        {
            var result = await import.ImportMasterCsvAsync(plantId, ownerOrgId, stream, ct).ConfigureAwait(false);
            await audit.LogAsync(
                action: "scada.master_imported",
                entityType: "ScadaImport",
                entityId: $"{plantId}:{DateTimeOffset.UtcNow:o}",
                payload: new { plantId, ownerOrgId, result },
                ct).ConfigureAwait(false);
            return Results.Ok(result);
        },
        currentUser, uploadOptions, ct);

    private static Task<IResult> UploadOperlogAsync(
        string plantId,
        HttpRequest request,
        IScadaImportService import,
        ICurrentUser currentUser,
        IAuditLogger audit,
        IOptions<SettlementUploadOptions> uploadOptions,
        CancellationToken ct) =>
        ProcessUploadAsync(plantId, request, async (stream, ownerOrgId) =>
        {
            var result = await import.ImportOperlogCsvAsync(plantId, ownerOrgId, stream, ct).ConfigureAwait(false);
            await audit.LogAsync(
                action: "scada.operlog_imported",
                entityType: "OperlogImport",
                entityId: $"{plantId}:{DateTimeOffset.UtcNow:o}",
                payload: new { plantId, ownerOrgId, result },
                ct).ConfigureAwait(false);
            return Results.Ok(result);
        },
        currentUser, uploadOptions, ct);

    /// <summary>
    /// Felles multipart-håndtering. Validerer Content-Type, plukker første fil-seksjon,
    /// resolver OwnerOrgId fra ICurrentUser (eller dev-default) og delegerer parsing.
    /// </summary>
    private static async Task<IResult> ProcessUploadAsync(
        string plantId,
        HttpRequest request,
        Func<Stream, string, Task<IResult>> handler,
        ICurrentUser currentUser,
        IOptions<SettlementUploadOptions> uploadOptions,
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
            return Results.Problem(
                title: "Ugyldig multipart-body",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }

        while (section is not null)
        {
            if (TryGetFileSection(section, out var fileName))
            {
                if (fileName is not null && !fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Problem(
                        title: "Ikke-støttet filtype",
                        detail: $"Forventet .csv, fikk '{fileName}'.",
                        statusCode: StatusCodes.Status415UnsupportedMediaType);
                }

                try
                {
                    return await handler(section.Body, ownerOrgId).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    return Results.Problem(
                        title: "Ugyldig CSV",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status400BadRequest);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return Results.Problem(
                        title: "Filen er for stor",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                }
            }

            try
            {
                section = await reader.ReadNextSectionAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                return Results.Problem(
                    title: "Ugyldig multipart-body",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        return Results.Problem(
            title: "Mangler fil",
            detail: "Fant ingen fil-seksjon i multipart/form-data.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static async ValueTask<object?> EnsureMaxBodySize(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var opts = ctx.HttpContext.RequestServices
            .GetRequiredService<IOptions<SettlementUploadOptions>>().Value;
        var feature = ctx.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is not null && !feature.IsReadOnly)
        {
            // SCADA-CSV blir vanligvis < 5 MB for hourly, men opptil 100 MB+ for raw.
            // Bruker samme MaxUploadBytes som settlements (default 25 MB).
            feature.MaxRequestBodySize = opts.MaxUploadBytes;
        }
        return await next(ctx).ConfigureAwait(false);
    }

    // ---- Helpers (kopiert fra SettlementsEndpoints — kunne refaktoreres til shared) ---

    private static bool IsMultipart(HttpRequest request, out string? boundary, out IResult? problem)
    {
        boundary = null;
        problem = null;
        if (!request.HasFormContentType
            || !MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            problem = Results.Problem(
                title: "Forventer multipart/form-data",
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
}
