using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Api.Options;
using KraftverkUptime.Core.DataCompleteness;
using KraftverkUptime.Core.Events;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Core.Storage;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Settlement;
using KraftverkUptime.Modules.Settlement.Jobs;
using KraftverkUptime.Modules.Settlement.Persistence;
using KraftverkUptime.Modules.Settlement.Quality;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Endepunkt for multi-anleggs settlement-import:
/// <c>POST /api/v{version}/settlements/multi-plant</c>
///
/// Mottar én Excel-fil med ≥ 2 anleggsfaner, parser alle, oppretter
/// eventuelle nye anlegg, og persisterer én <c>settlement_imports</c>-rad
/// per anlegg. Klassifisering trigges async per anlegg via
/// <c>SettlementImportedEvent</c>, akkurat som single-plant-flyten.
///
/// Kontrakten:
/// <list type="bullet">
///   <item>Multipart/form-data med "file"-seksjon</item>
///   <item>Content-Type: <c>application/vnd.openxmlformats-officedocument.spreadsheetml.sheet</c></item>
///   <item>Krever <c>PlantAdmin</c>-policy (V1: returnerer "allow" inntil Entra ID kobles til).</item>
///   <item>Returnerer 200 med array av <see cref="MultiPlantImportResult"/></item>
/// </list>
/// </summary>
public static class MultiPlantSettlementsEndpoints
{
    public static IEndpointRouteBuilder MapMultiPlantSettlementsV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/settlements")
            .WithTags("Settlements")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapPost("/multi-plant", UploadMultiPlantAsync)
            .WithName("UploadMultiPlantSettlement")
            .WithSummary("Laster opp én Excel med flere anlegg og oppretter en import-rad per anlegg.")
            .DisableAntiforgery()
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .AddEndpointFilter(async (ctx, next) =>
            {
                var opts = ctx.HttpContext.RequestServices
                    .GetRequiredService<IOptions<SettlementUploadOptions>>().Value;
                var feature = ctx.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is not null && !feature.IsReadOnly)
                {
                    feature.MaxRequestBodySize = opts.MaxUploadBytes;
                }
                return await next(ctx).ConfigureAwait(false);
            })
            .Produces<MultiPlantImportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        return endpoints;
    }

    private static async Task<IResult> UploadMultiPlantAsync(
        HttpRequest request,
        ISettlementParser parser,
        IFileStorage fileStorage,
        ISettlementImportRecorder importRecorder,
        IEventPublisher events,
        IAuditLogger audit,
        IDataImportLogger dataImportLogger,
        DataQualityReportBuilder qualityBuilder,
        KraftverkDbContext db,
        ICurrentUser currentUser,
        IOptions<SettlementUploadOptions> uploadOptions,
        TimeProvider clock,
        ILogger<MultiPlantImportLogger> logger,
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
            if (TryGetFileSection(section, out var fileName, out var contentType))
            {
                if (!IsXlsxContentType(contentType))
                {
                    return Results.Problem(title: "Ikke-støttet filtype",
                        detail: $"Forventer .xlsx (multipart Content-Type '{contentType}').",
                        statusCode: StatusCodes.Status415UnsupportedMediaType);
                }

                return await ProcessFileAsync(
                    section.Body, fileName, ownerOrgId, parser, fileStorage,
                    importRecorder, events, audit, dataImportLogger, qualityBuilder, db, clock, logger, ct).ConfigureAwait(false);
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

    private static async Task<IResult> ProcessFileAsync(
        Stream body, string? fileName, string ownerOrgId,
        ISettlementParser parser,
        IFileStorage fileStorage,
        ISettlementImportRecorder importRecorder,
        IEventPublisher events,
        IAuditLogger audit,
        IDataImportLogger dataImportLogger,
        DataQualityReportBuilder qualityBuilder,
        KraftverkDbContext db,
        TimeProvider clock,
        ILogger logger,
        CancellationToken ct)
    {
        // Materialiser body en gang slik at vi både kan lagre til blob og
        // parse uten å seek-e to ganger (multipart-strømmen er forward-only).
        var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        var fileHash = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
        buffer.Position = 0;

        // Lagre én kopi av fila i blob — alle plant-imports peker til samme blob.
        var safeFileName = SanitizeFileName(fileName);
        var timestamp = clock.GetUtcNow().ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        var blobPath = $"settlements/{ownerOrgId}/_multi/{timestamp}-{safeFileName}";
        await fileStorage.PutAsync(blobPath, buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        // Parse alle anleggene i workbooket
        var parsedAll = await parser.ParseAllAsync(buffer, ct).ConfigureAwait(false);
        if (parsedAll.Count == 0)
        {
            return Results.Problem(title: "Ingen anleggs-faner",
                detail: "Workbooket inneholdt ingen tolkbare anleggs-faner.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var traceId = Activity.Current?.TraceId.ToString();
        var results = new List<MultiPlantImportResult>(parsedAll.Count);
        var skippedNames = new List<string>();

        foreach (var parsed in parsedAll)
        {
            if (string.IsNullOrEmpty(parsed.PlantId))
            {
                // Single-plant fallback — denne endepunkten er for multi-plant.
                // Skip og logg.
                skippedNames.Add(parsed.PlantName);
                logger.LogWarning(
                    "Multi-plant-import: hopper over '{Name}' fordi PlantId mangler (enkelt-plant-fil?). " +
                    "Bruk /api/v1/plants/{{plantId}}/settlements for enkelt-plant-format.",
                    parsed.PlantName);
                continue;
            }

            var created = await PlantPortfolioSeeder
                .EnsurePlantExistsAsync(db, parsed.PlantId, parsed.PlantName, logger, ct)
                .ConfigureAwait(false);

            // Per-plant idempotency-key: SHA256(file_hash | plant_id) gir at
            // samme fil opp-lastet to ganger gir samme key per plant, og to
            // ulike multi-plant-filer som dekker samme periode for et anlegg
            // får forskjellige keys.
            var perPlantKey = ComputePerPlantIdempotencyKey(fileHash, parsed.PlantId);

            var (quality, _) = qualityBuilder.Build(parsed);

            await importRecorder.RecordAsync(new SettlementImportRecord
            {
                OwnerOrgId = ownerOrgId,
                PlantId = parsed.PlantId,
                IdempotencyKey = perPlantKey,
                BlobPath = blobPath,
                PlantName = parsed.PlantName,
                SchemaVersion = parsed.SchemaVersion,
                PeriodStartUtc = parsed.PeriodStartUtc,
                PeriodEndUtc = parsed.PeriodEndUtc,
                HourCount = parsed.Hourly.Count,
                IssueCount = parsed.Issues.Count,
                ImportedAtUtc = clock.GetUtcNow(),
                CorrelationId = traceId,
            }, ct).ConfigureAwait(false);

            await audit.LogAsync(
                action: "settlement.imported",
                entityType: "SettlementImport",
                entityId: perPlantKey,
                payload: new
                {
                    parsed.PlantId,
                    parsed.PlantName,
                    parsed.SchemaVersion,
                    Source = "multi-plant",
                    HourCount = parsed.Hourly.Count,
                    IssueCount = parsed.Issues.Count,
                    PlantCreated = created,
                    quality.HoursAccepted,
                    quality.HoursFlagged,
                    quality.HoursRejected,
                },
                ct).ConfigureAwait(false);

            // SPEC-IMPORT-COMPLETENESS: logg per-plant til data_imports.
            // Settlement-rad inkluderer Hydrogrid-plan-status i Notes
            // (drifts-leders 2026-05-03-bekreftelse: kun 3 source types).
            var expectedHours = (int)Math.Round((parsed.PeriodEndUtc - parsed.PeriodStartUtc).TotalHours);
            var settlementCoverage = expectedHours > 0
                ? Math.Min(1.0, parsed.Hourly.Count / (double)expectedHours)
                : 1.0;
            var planRows = parsed.Hourly.Count(r => r.ProduksjonplanMwh.HasValue);
            var notes = parsed.Issues.Count > 0
                ? $"{parsed.Issues.Count} avvik"
                : (planRows > 0 ? $"Hydrogrid-plan: {planRows}/{parsed.Hourly.Count} timer" : null);

            try
            {
                await dataImportLogger.LogAsync(new DataImportLogEntry(
                    PlantId: parsed.PlantId,
                    SourceType: "settlement",
                    PeriodFromUtc: parsed.PeriodStartUtc,
                    PeriodToUtc: parsed.PeriodEndUtc,
                    FileName: fileName,
                    FileHash: perPlantKey,
                    RowsImported: parsed.Hourly.Count,
                    CoveragePct: settlementCoverage,
                    UserId: "system",
                    Notes: notes
                ), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "data_imports-logging feilet for {Plant} i multi-plant-import. Selve importen er på plass.",
                    parsed.PlantId);
            }

            await events.PublishAsync(new SettlementImportedEvent
            {
                PlantId = parsed.PlantId,
                OwnerOrgId = ownerOrgId,
                BlobPath = blobPath,
                IdempotencyKey = perPlantKey,
                PeriodStartUtc = parsed.PeriodStartUtc,
                PeriodEndUtc = parsed.PeriodEndUtc,
                HourCount = parsed.Hourly.Count,
                IssueCount = parsed.Issues.Count,
                CorrelationId = traceId,
            }, ct).ConfigureAwait(false);

            results.Add(new MultiPlantImportResult(
                PlantId: parsed.PlantId,
                PlantName: parsed.PlantName,
                IdempotencyKey: perPlantKey,
                HourCount: parsed.Hourly.Count,
                IssueCount: parsed.Issues.Count,
                PlantCreated: created));
        }

        logger.LogInformation(
            "Multi-plant-import fullført: {ImportCount} anlegg av {ParsedCount} parsed (skippet: {Skipped}). Blob: {Blob}",
            results.Count, parsedAll.Count, skippedNames.Count, blobPath);

        return Results.Ok(new MultiPlantImportResponse(
            BlobPath: blobPath,
            ImportCount: results.Count,
            SkippedCount: skippedNames.Count,
            Imports: results));
    }

    private static string ComputePerPlantIdempotencyKey(string fileHash, string plantId)
    {
        var combined = $"{fileHash}|{plantId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveOwnerOrgId(ICurrentUser currentUser, SettlementUploadOptions opts)
    {
        if (!string.Equals(currentUser.OrgId, "system", StringComparison.Ordinal))
        {
            return currentUser.OrgId;
        }
        return string.IsNullOrWhiteSpace(opts.DevDefaultOwnerOrgId)
            ? throw new InvalidOperationException("Settlements:DevDefaultOwnerOrgId må være satt for anonym authn.")
            : opts.DevDefaultOwnerOrgId;
    }

    private static bool IsXlsxContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return true; // tillat manglende — sanitize på filending

        var mediaType = contentType.Split(';', 2, StringSplitOptions.TrimEntries)[0];
        return mediaType.Equals("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/vnd.ms-excel", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "settlement.xlsx";
        var span = Path.GetFileName(name.AsSpan());
        var buf = new char[span.Length];
        var i = 0;
        foreach (var c in span)
        {
            buf[i++] = (char.IsLetterOrDigit(c) || c is '.' or '-' or '_') ? c : '_';
        }
        var cleaned = new string(buf, 0, i).ToLowerInvariant();
        return string.IsNullOrEmpty(cleaned) ? "settlement.xlsx" : cleaned;
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

    private static bool TryGetFileSection(MultipartSection section, out string? fileName, out string? contentType)
    {
        fileName = null;
        contentType = section.ContentType;
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

/// <summary>Marker for ILogger-kategori. Holder den ute av andre namespacer.</summary>
public sealed class MultiPlantImportLogger { }

public sealed record MultiPlantImportResult(
    string PlantId,
    string PlantName,
    string IdempotencyKey,
    int HourCount,
    int IssueCount,
    bool PlantCreated);

public sealed record MultiPlantImportResponse(
    string BlobPath,
    int ImportCount,
    int SkippedCount,
    IReadOnlyList<MultiPlantImportResult> Imports);
