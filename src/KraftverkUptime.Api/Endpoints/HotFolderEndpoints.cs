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

        group.MapPost("/scan-now", ScanNowAsync)
            .WithName("HotFolderScanNow")
            .WithSummary("Trigger manuell scan av auto-import-mappa (Skann nå-knapp).")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<HotFolderScanResult>(StatusCodes.Status200OK);

        return endpoints;
    }

    private static async Task<IResult> ScanNowAsync(
        HotFolderQueue queue,
        HotFolderOptions options,
        CancellationToken ct)
    {
        if (!options.Enabled)
        {
            return Results.Ok(new HotFolderScanResult(
                Triggered: false,
                Message: "Hot-folder er deaktivert i konfigurasjonen (HotFolder:Enabled = false).",
                WaitingBefore: queue.GetQueue().Count));
        }

        var watcher = HotFolderWatcher.Current;
        if (watcher is null)
        {
            return Results.Ok(new HotFolderScanResult(
                Triggered: false,
                Message: "Watcher er ikke initialisert ennå — prøv igjen om noen sekunder.",
                WaitingBefore: queue.GetQueue().Count));
        }

        var waitingBefore = queue.GetQueue().Count;
        await watcher.TriggerScanAsync(ct).ConfigureAwait(false);
        return Results.Ok(new HotFolderScanResult(
            Triggered: true,
            Message: "Scan trigget — sjekk køen for status.",
            WaitingBefore: waitingBefore));
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

public sealed record HotFolderScanResult(
    bool Triggered,
    string Message,
    int WaitingBefore);
