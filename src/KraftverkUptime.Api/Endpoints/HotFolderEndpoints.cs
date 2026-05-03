using Asp.Versioning;
using Asp.Versioning.Builder;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Infrastructure.HotFolder;

namespace KraftverkUptime.Api.Endpoints;

/// <summary>
/// API for hot-folder-køen og siste prosesserte filer (SPEC-AUTO-IMPORT-FOLDER).
/// Brukes av auto-import-banneren på /data-import for å vise live-status.
/// </summary>
public static class HotFolderEndpoints
{
    public static IEndpointRouteBuilder MapHotFolderV1(
        this IEndpointRouteBuilder endpoints, ApiVersionSet versionSet)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/hot-folder")
            .WithTags("HotFolder")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0));

        group.MapGet("/queue", GetQueue)
            .WithName("GetHotFolderQueue")
            .WithSummary("Filer som ligger i auto-import-mappa og venter på prosessering.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<HotFolderStatusResponse>(StatusCodes.Status200OK);

        return endpoints;
    }

    private static IResult GetQueue(HotFolderQueue queue, HotFolderOptions options)
    {
        return Results.Ok(new HotFolderStatusResponse(
            Enabled: options.Enabled,
            RootPath: options.RootPath,
            Waiting: queue.GetQueue(),
            Recent: queue.GetRecent(20)));
    }
}

public sealed record HotFolderStatusResponse(
    bool Enabled,
    string RootPath,
    IReadOnlyList<HotFolderQueueEntry> Waiting,
    IReadOnlyList<HotFolderRecentEntry> Recent);
