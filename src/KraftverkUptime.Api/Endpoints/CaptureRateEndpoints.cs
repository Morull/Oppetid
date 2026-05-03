using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.CaptureRate;
using Microsoft.EntityFrameworkCore;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Capture rate-endepunkter:
///   GET /api/v1/plants/{plantId}/capture-rate?from=&amp;to=
///   GET /api/v1/plants/{plantId}/capture-rate/monthly?from=&amp;to=
///   GET /api/v1/plants/{plantId}/capture-rate/daily?from=&amp;to=
///
/// Anlegg-uavhengig — fungerer for alle 11 plants så lenge settlement-data
/// + market_prices er importert. Fasit-tall i specens akseptansekriterier.
/// </summary>
public static class CaptureRateEndpoints
{
    public static IEndpointRouteBuilder MapCaptureRateV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants/{plantId}/capture-rate")
            .WithTags("CaptureRate")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/", GetAsync)
            .WithName("GetCaptureRate")
            .WithSummary("Aggregert capture rate for et anlegg over en periode.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<CaptureRateCalculator.CaptureRateResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/monthly", GetMonthlyAsync)
            .WithName("GetCaptureRateMonthly")
            .WithSummary("Månedlig capture rate-serie for graf på /capture-rate-side.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<MonthlyCaptureRate>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/daily", GetDailyAsync)
            .WithName("GetCaptureRateDaily")
            .WithSummary("Daglig serie for scatter/histogram på /capture-rate-side.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<IReadOnlyList<DailyCaptureRate>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        ICaptureRateQueryService service,
        KraftverkDbContext db,
        IQueryContext queryContext,
        CancellationToken ct)
    {
        if (!TryValidatePeriod(from, to, out var fromUtc, out var toUtc, out var problem))
        {
            return problem!;
        }

        var plantExists = await queryContext.Apply(db.Plants.AsQueryable())
            .AnyAsync(p => p.Id == plantId, ct).ConfigureAwait(false);
        if (!plantExists)
        {
            return Results.Problem(
                title: "Anlegg ikke funnet",
                detail: $"Plant '{plantId}' eksisterer ikke.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var result = await service.GetForPlantAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetMonthlyAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        ICaptureRateQueryService service,
        CancellationToken ct)
    {
        if (!TryValidatePeriod(from, to, out var fromUtc, out var toUtc, out var problem))
        {
            return problem!;
        }
        var rows = await service.GetMonthlySeriesAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetDailyAsync(
        string plantId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        ICaptureRateQueryService service,
        CancellationToken ct)
    {
        if (!TryValidatePeriod(from, to, out var fromUtc, out var toUtc, out var problem))
        {
            return problem!;
        }
        var rows = await service.GetDailySeriesAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
        return Results.Ok(rows);
    }

    private static bool TryValidatePeriod(
        DateTimeOffset? from, DateTimeOffset? to,
        out DateTimeOffset fromUtc, out DateTimeOffset toUtc,
        out IResult? problem)
    {
        fromUtc = default;
        toUtc = default;
        problem = null;

        if (!from.HasValue || !to.HasValue)
        {
            problem = Results.Problem(title: "Manglende periode",
                detail: "Både 'from' og 'to' må oppgis (ISO-8601, UTC).",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
        fromUtc = from.Value.ToUniversalTime();
        toUtc = to.Value.ToUniversalTime();
        if (toUtc <= fromUtc)
        {
            problem = Results.Problem(title: "Ugyldig periode",
                detail: "'to' må være etter 'from'.",
                statusCode: StatusCodes.Status400BadRequest);
            return false;
        }
        return true;
    }
}
