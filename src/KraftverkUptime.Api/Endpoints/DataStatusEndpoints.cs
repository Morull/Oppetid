using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Modules.Reporting.DataCompleteness;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// API-endepunkter for SPEC-IMPORT-COMPLETENESS:
///   GET /api/v1/data-status/matrix?from=&amp;to=
///   GET /api/v1/data-status/overdue
///   GET /api/v1/data-status/summary
///
/// Brukes av <c>/data-status</c>-siden, ukentlig digest-job og eventuelle
/// eksterne integrasjoner. Per-plant-timeline er ikke et eget endepunkt
/// i v1 — UI-en filtrerer matrix-responsen lokalt.
/// </summary>
public static class DataStatusEndpoints
{
    public static IEndpointRouteBuilder MapDataStatusV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/data-status")
            .WithTags("DataStatus")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/matrix", GetMatrixAsync)
            .WithName("GetDataCompletenessMatrix")
            .WithSummary("Komplett dekningsmatrise for [from, to) — anlegg × kilder × perioder.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<DataCompletenessMatrixResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/overdue", GetOverdueAsync)
            .WithName("GetDataCompletenessOverdue")
            .WithSummary("Liste over forventede importer som mangler og er forfalt (siste 12 mnd).")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<MissingImport>>(StatusCodes.Status200OK);

        group.MapGet("/summary", GetSummaryAsync)
            .WithName("GetDataCompletenessSummary")
            .WithSummary("Aggregert sammendrag for forrige + denne måneden.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<DataCompletenessSummary>(StatusCodes.Status200OK);

        group.MapGet("/recent", GetRecentImportsAsync)
            .WithName("GetRecentImports")
            .WithSummary("Lister nylige importer (default siste 24 timer) — brukes av auto-import-infobar.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<RecentImport>>(StatusCodes.Status200OK);

        return endpoints;
    }

    private static async Task<IResult> GetMatrixAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        IDataCompletenessQueryService service,
        CancellationToken ct)
    {
        if (!from.HasValue || !to.HasValue)
        {
            return Results.Problem(title: "Manglende periode",
                detail: "Både 'from' og 'to' må oppgis (ISO-8601, UTC).",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var fromUtc = from.Value.ToUniversalTime();
        var toUtc = to.Value.ToUniversalTime();
        if (toUtc <= fromUtc)
        {
            return Results.Problem(title: "Ugyldig periode",
                detail: "'to' må være etter 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var matrix = await service.GetMatrixAsync(fromUtc, toUtc, ct).ConfigureAwait(false);

        // Konverter dictionary-nøkkelen til en list-struktur for JSON
        // (System.Text.Json støtter ikke composite-key-dicts som default).
        var cells = matrix.Cells
            .Select(kv => new DataCompletenessCellDto(
                kv.Key.PlantId,
                kv.Key.SourceType,
                kv.Key.Period,
                kv.Value.Status,
                kv.Value.LastImportedAt,
                kv.Value.CoveragePct,
                kv.Value.ImportCount,
                kv.Value.ImportPeriodFromUtc,
                kv.Value.ImportPeriodToUtc,
                kv.Value.RowsImported,
                kv.Value.FileName,
                kv.Value.Notes,
                kv.Value.Threshold))
            .ToList();

        return Results.Ok(new DataCompletenessMatrixResponse(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            PlantIds: matrix.PlantIds,
            SourceTypes: matrix.SourceTypes,
            Periods: matrix.Periods,
            Cells: cells));
    }

    private static async Task<IResult> GetOverdueAsync(
        IDataCompletenessQueryService service,
        CancellationToken ct)
    {
        var overdue = await service.GetOverdueAsync(ct).ConfigureAwait(false);
        return Results.Ok(overdue);
    }

    private static async Task<IResult> GetSummaryAsync(
        IDataCompletenessQueryService service,
        CancellationToken ct)
    {
        var summary = await service.GetWeeklySummaryAsync(ct).ConfigureAwait(false);
        return Results.Ok(summary);
    }

    private static async Task<IResult> GetRecentImportsAsync(
        int? hours,
        int? limit,
        IDataCompletenessQueryService service,
        CancellationToken ct)
    {
        var window = TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 168));
        var max = Math.Clamp(limit ?? 50, 1, 500);
        var rows = await service.GetRecentImportsAsync(window, max, ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }
}

/// <summary>
/// JSON-vennlig matrise-respons. Cells eksponeres som flat liste i stedet
/// for composite-key-dictionary slik at <c>System.Text.Json</c> kan serialisere
/// uten custom converter.
/// </summary>
public sealed record DataCompletenessMatrixResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<string> PlantIds,
    IReadOnlyList<string> SourceTypes,
    IReadOnlyList<DateTimeOffset> Periods,
    IReadOnlyList<DataCompletenessCellDto> Cells);

public sealed record DataCompletenessCellDto(
    string PlantId,
    string SourceType,
    DateTimeOffset Period,
    string Status,
    DateTimeOffset? LastImportedAt,
    double? CoveragePct,
    int ImportCount,
    DateTimeOffset? ImportPeriodFromUtc = null,
    DateTimeOffset? ImportPeriodToUtc = null,
    int? RowsImported = null,
    string? FileName = null,
    string? Notes = null,
    double? Threshold = null);
