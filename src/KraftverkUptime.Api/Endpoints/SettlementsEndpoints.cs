using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Api.Contracts;
using KraftverkUptime.Api.Options;
using KraftverkUptime.Core.Reporting;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Endepunkt for <c>POST /api/v{version}/plants/{plantId}/settlements</c> – tar imot
/// en multipart/form-data-opplasting, strømmer filen inn i <c>IFileStorage</c> og
/// legger en <c>ParseSettlementJob</c> på kø.
///
/// Avklaringer for denne iterasjonen:
/// <list type="bullet">
///   <item>Authn: <see cref="Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute"/> +
///         TODO for Entra ID i Steg 5.</item>
///   <item>IdempotencyKey: server beregner SHA256 av strømmet body.</item>
///   <item>Body: multipart, maks 25 MB default (konfigurerbart via
///         <see cref="SettlementUploadOptions.MaxUploadBytes"/>), strømmet til blob.</item>
///   <item>OwnerOrgId: dev-default fra <see cref="SettlementUploadOptions.DevDefaultOwnerOrgId"/>
///         inntil claims-basert authn er koblet til.</item>
/// </list>
/// </summary>
public static class SettlementsEndpoints
{
    public static IEndpointRouteBuilder MapSettlementsV1(this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/settlements")
            .WithTags("Settlements")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapPost("/", UploadAsync)
            .WithName("UploadSettlement")
            .WithSummary("Laster opp en portaleksport og starter asynkron parsing.")
            .DisableAntiforgery()
            .AllowAnonymous() // TODO(Steg 6): erstatt med .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .AddEndpointFilter(async (ctx, next) =>
            {
                // Hever Kestrel-grensen fra default 30 MB til konfigurert verdi.
                var opts = ctx.HttpContext.RequestServices
                    .GetRequiredService<IOptions<SettlementUploadOptions>>().Value;

                var feature = ctx.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is not null && !feature.IsReadOnly)
                {
                    feature.MaxRequestBodySize = opts.MaxUploadBytes;
                }

                return await next(ctx).ConfigureAwait(false);
            })
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        group.MapGet("/", ListAsync)
            .WithName("ListSettlements")
            .WithSummary("Lister importer for et anlegg, valgfritt filtrert på periode-overlapp.")
            .AllowAnonymous() // TODO(Steg 6): .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<SettlementImportDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/{idempotencyKey}/report", GetReportAsync)
            .WithName("GetSettlementReport")
            .WithSummary("Henter ferdig-klassifisert UptimeReport som JSON.")
            .AllowAnonymous() // TODO(Steg 6): .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<UptimeReport>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{idempotencyKey}/report/xlsx", GetReportXlsxAsync)
            .WithName("GetSettlementReportXlsx")
            .WithSummary("Laster ned UptimeReport som Excel-arbeidsbok (.xlsx).")
            .AllowAnonymous() // TODO(Steg 6): .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces(StatusCodes.Status200OK, contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    // ---- GET /api/v1/plants/{plantId}/settlements -----------------------------

    private static async Task<IResult> ListAsync(
        string plantId,
        ISettlementImportRecorder recorder,
        IUptimeReportStore reportStore,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int? limit,
        CancellationToken ct)
    {
        if (from.HasValue != to.HasValue)
        {
            return Results.Problem(
                title: "Ugyldig filter",
                detail: "'from' og 'to' må enten begge settes eller begge utelates.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var effectiveLimit = Math.Clamp(limit ?? 50, 1, 500);

        var rows = await recorder.ListForPlantAsync(plantId, from, to, effectiveLimit, ct).ConfigureAwait(false);

        var result = new List<SettlementImportDto>(rows.Count);
        foreach (var row in rows)
        {
            var available = await reportStore
                .GetAsync(row.OwnerOrgId, row.PlantId, row.IdempotencyKey, ct)
                .ConfigureAwait(false) is not null;

            result.Add(new SettlementImportDto(
                PlantId: row.PlantId,
                IdempotencyKey: row.IdempotencyKey,
                PlantName: row.PlantName,
                SchemaVersion: row.SchemaVersion,
                PeriodStartUtc: row.PeriodStartUtc,
                PeriodEndUtc: row.PeriodEndUtc,
                HourCount: row.HourCount,
                IssueCount: row.IssueCount,
                ImportedAtUtc: row.ImportedAtUtc,
                ReportAvailable: available));
        }

        return Results.Ok(result);
    }

    // ---- GET /api/v1/plants/{plantId}/settlements/{idempotencyKey}/report ----

    private static async Task<IResult> GetReportAsync(
        string plantId,
        string idempotencyKey,
        ISettlementImportRecorder recorder,
        IUptimeReportStore reportStore,
        CancellationToken ct)
    {
        var record = await recorder
            .FindByIdempotencyKeyAsync(plantId, idempotencyKey, ct)
            .ConfigureAwait(false);
        if (record is null)
        {
            return Results.Problem(
                title: "Import ikke funnet",
                detail: $"Ingen import med idempotencyKey={idempotencyKey} for plant {plantId}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var report = await reportStore
            .GetAsync(record.OwnerOrgId, record.PlantId, record.IdempotencyKey, ct)
            .ConfigureAwait(false);
        if (report is null)
        {
            return Results.Problem(
                title: "Rapport ikke klar",
                detail: "Importen er registrert, men klassifiseringsjobben er ikke fullført ennå.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Ok(report);
    }

    // ---- GET /api/v1/plants/{plantId}/settlements/{idempotencyKey}/report/xlsx

    private static async Task<IResult> GetReportXlsxAsync(
        string plantId,
        string idempotencyKey,
        ISettlementImportRecorder recorder,
        IUptimeReportStore reportStore,
        IEnumerable<IReportRenderer> renderers,
        CancellationToken ct)
    {
        var record = await recorder
            .FindByIdempotencyKeyAsync(plantId, idempotencyKey, ct)
            .ConfigureAwait(false);
        if (record is null)
        {
            return Results.Problem(
                title: "Import ikke funnet",
                statusCode: StatusCodes.Status404NotFound);
        }

        var report = await reportStore
            .GetAsync(record.OwnerOrgId, record.PlantId, record.IdempotencyKey, ct)
            .ConfigureAwait(false);
        if (report is null)
        {
            return Results.Problem(
                title: "Rapport ikke klar",
                detail: "Klassifiseringsjobben er ikke fullført ennå.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var xlsxRenderer = renderers.FirstOrDefault(r => string.Equals(r.Format, "xlsx", StringComparison.OrdinalIgnoreCase));
        if (xlsxRenderer is null)
        {
            return Results.Problem(
                title: "Mangler XLSX-renderer",
                detail: "Ingen IReportRenderer er registrert for format 'xlsx'.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var stream = await xlsxRenderer.RenderAsync(report, ct).ConfigureAwait(false);
        var fileName = $"{record.PlantId}-{record.PeriodStartUtc:yyyy-MM}-uptime.xlsx";
        return Results.File(
            fileStream: stream,
            contentType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileDownloadName: fileName);
    }

    private static async Task<IResult> UploadAsync(
        string plantId,
        HttpRequest request,
        SettlementUploadHandler handler,
        CancellationToken ct)
    {
        if (!IsMultipart(request, out var boundary, out var problem))
        {
            return problem!;
        }

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
            if (TryGetFileSection(section, out var fileName, out var contentType))
            {
                if (!handler.IsContentTypeAllowed(contentType))
                {
                    return Results.Problem(
                        title: "Ikke-støttet filtype",
                        detail: $"Content-Type '{contentType}' er ikke tillatt. Send en .xlsx-fil.",
                        statusCode: StatusCodes.Status415UnsupportedMediaType);
                }

                try
                {
                    var result = await handler.HandleAsync(
                        plantId, section.Body, fileName, correlationId: null, ct).ConfigureAwait(false);

                    return Results.Accepted(
                        uri: $"/api/v1/plants/{plantId}/settlements/{result.IdempotencyKey}",
                        value: new
                        {
                            result.PlantId,
                            result.OwnerOrgId,
                            result.BlobPath,
                            result.IdempotencyKey,
                            result.CorrelationId,
                        });
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
            detail: "Fant ingen fil-seksjon i multipart/form-data. Bruk feltnavn 'file' og sett filename.",
            statusCode: StatusCodes.Status400BadRequest);
    }

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
                detail: "Request må sendes som multipart/form-data med en filseksjon.",
                statusCode: StatusCodes.Status415UnsupportedMediaType);
            return false;
        }

        var boundaryValue = HeaderUtilities.RemoveQuotes(mediaType.Boundary).ToString();
        if (string.IsNullOrEmpty(boundaryValue))
        {
            problem = Results.Problem(
                title: "Mangler multipart boundary",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        boundary = boundaryValue;
        return true;
    }

    private static bool TryGetFileSection(
        MultipartSection section,
        out string? fileName,
        out string? contentType)
    {
        fileName = null;
        contentType = section.ContentType;

        if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
        {
            return false;
        }

        if (!disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // En fil-seksjon har enten filename= eller filename*= satt.
        var starName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar).ToString();
        var plainName = HeaderUtilities.RemoveQuotes(disposition.FileName).ToString();
        var rawName = !string.IsNullOrEmpty(starName) ? starName : plainName;

        if (string.IsNullOrWhiteSpace(rawName))
        {
            return false;
        }

        fileName = rawName;
        return true;
    }
}
