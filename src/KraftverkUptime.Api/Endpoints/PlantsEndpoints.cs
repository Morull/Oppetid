using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Api.Paging;
using KraftverkUptime.Core.Paging;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// Enkel plant-endepunkt for å demonstrere pagination, policies og IQueryContext i v1.
/// Domenespesifikke endepunkter legges til per modul i Prompt 2.
/// </summary>
public static class PlantsEndpoints
{
    public static IEndpointRouteBuilder MapPlantsV1(this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/plants")
            .WithTags("Plants")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .RequireAuthorization(AuthorizationPolicies.PlantReader);

        group.MapGet("/", async (
            KraftverkDbContext db,
            IQueryContext queryContext,
            IOptions<PaginationOptions> pagination,
            int? pageSize,
            CancellationToken ct) =>
        {
            var size = PaginationExtensions.ClampPageSize(pageSize, pagination.Value);

            var items = await queryContext
                .Apply(db.Plants.AsQueryable())
                .OrderBy(p => p.Id)
                .Take(size + 1)
                .ToListAsync(ct);

            string? next = null;
            if (items.Count > size)
            {
                next = items[size].Id;
                items = items.Take(size).ToList();
            }

            var dtos = items
                .Select(p => (object)new { p.Id, p.Name, Type = p.Type.ToString(), p.InstalledCapacityMw, p.TimeZone })
                .ToList();

            return Results.Ok(new PagedResult<object>(dtos, next, size));
        });

        return endpoints;
    }
}
