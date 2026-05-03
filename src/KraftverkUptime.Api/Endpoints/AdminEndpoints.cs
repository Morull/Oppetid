using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Events;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Settlement.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Admin-endepunkter for vedlikehold og engangsoperasjoner som ikke passer
/// inn i de domenespesifikke gruppene. Krever <see cref="AuthorizationPolicies.SystemAdmin"/>
/// — kryssanlegg-operasjoner som påvirker hele org. V1: policy returnerer
/// "allow" frem til Entra ID kobles til.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin")
            .WithTags("Admin")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapPost("/market-prices/rebuild-from-settlement", RebuildMarketPricesAsync)
            .WithName("RebuildMarketPrices")
            .WithSummary("Re-publiser SettlementImportedEvent for alle eksisterende imports — fyller core.market_prices via MarketPriceUpsertHandler.")
            .RequireAuthorization(AuthorizationPolicies.SystemAdmin)
            .Produces<RebuildMarketPricesResult>(StatusCodes.Status200OK);

        return endpoints;
    }

    private static async Task<IResult> RebuildMarketPricesAsync(
        KraftverkDbContext db,
        IEventPublisher events,
        IAuditLogger audit,
        ILogger<AdminEndpointsLogger> logger,
        CancellationToken ct)
    {
        // Hent alle eksisterende imports og publiser et SettlementImportedEvent
        // per stk. Eksisterende handlere (ClassifyOnImportedHandler,
        // MarketPriceUpsertHandler) plukker det opp parallelt — siden begge
        // er idempotente er det trygt å kjøre flere ganger.
        var imports = await db.SettlementImports
            .AsNoTracking()
            .Where(i => i.PeriodStartUtc > DateTimeOffset.MinValue)
            .OrderBy(i => i.ImportedAtUtc)
            .ToListAsync(ct).ConfigureAwait(false);

        var published = 0;
        var skipped = 0;

        foreach (var imp in imports)
        {
            if (string.IsNullOrEmpty(imp.PlantId))
            {
                skipped++;
                continue;
            }
            await events.PublishAsync(new SettlementImportedEvent
            {
                PlantId = imp.PlantId,
                OwnerOrgId = imp.OwnerOrgId,
                BlobPath = imp.BlobPath,
                IdempotencyKey = imp.IdempotencyKey,
                PeriodStartUtc = imp.PeriodStartUtc,
                PeriodEndUtc = imp.PeriodEndUtc,
                HourCount = imp.HourCount,
                IssueCount = imp.IssueCount,
                CorrelationId = imp.CorrelationId,
            }, ct).ConfigureAwait(false);
            published++;
        }

        logger.LogInformation(
            "Admin rebuild-market-prices: re-publisert {Published} events ({Skipped} skippet).",
            published, skipped);

        await audit.LogAsync(
            action: "admin.market_prices_rebuild",
            entityType: "MarketPrices",
            entityId: "global",
            payload: new { published, skipped, totalImports = imports.Count },
            ct).ConfigureAwait(false);

        return Results.Ok(new RebuildMarketPricesResult(published, skipped));
    }
}

/// <summary>Marker for ILogger-kategori.</summary>
public sealed class AdminEndpointsLogger { }

public sealed record RebuildMarketPricesResult(int EventsPublished, int Skipped);
