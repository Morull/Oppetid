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

        group.MapPost("/retry-quarantine", RetryQuarantineAsync)
            .WithName("HotFolderRetryQuarantine")
            .WithSummary("Flytt alle karantene-filer tilbake til hot-folder for ny prosessering.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<HotFolderRetryResult>(StatusCodes.Status200OK);

        return endpoints;
    }

    private static async Task<IResult> RetryQuarantineAsync(
        HotFolderOptions options,
        HotFolderQueue queue,
        CancellationToken ct)
    {
        if (!options.Enabled)
        {
            return Results.Ok(new HotFolderRetryResult(0, "Hot-folder deaktivert."));
        }

        var quarantineRoot = Path.Combine(options.RootPath, options.QuarantineFolderName);
        if (!Directory.Exists(quarantineRoot))
        {
            return Results.Ok(new HotFolderRetryResult(0, "Ingen karantene-mappe."));
        }

        // Flytt alle .xlsx/.csv tilbake til root, slett tilhørende .error.txt
        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(quarantineRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".xlsx" or ".xls" or ".csv")) continue;

            var dest = Path.Combine(options.RootPath, Path.GetFileName(file));
            try
            {
                if (File.Exists(dest))
                {
                    // Unngå overwrite — legg til timestamp-suffiks
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var newName = $"{stem}_retry{DateTime.UtcNow:HHmmss}{ext}";
                    dest = Path.Combine(options.RootPath, newName);
                }
                File.Move(file, dest);
                // Slett tilhørende .error.txt hvis finnes
                var errorFile = file + ".error.txt";
                if (File.Exists(errorFile)) File.Delete(errorFile);
                moved++;
            }
            catch
            {
                // Hopp over filer vi ikke får tatt — neste retry tar dem
            }
        }

        // Trigge en scan etter retry så filene plukkes opp umiddelbart
        var watcher = HotFolderWatcher.Current;
        if (watcher is not null && moved > 0)
        {
            await watcher.TriggerScanAsync(ct).ConfigureAwait(false);
        }

        return Results.Ok(new HotFolderRetryResult(moved,
            moved > 0
                ? $"{moved} filer flyttet tilbake fra karantene — scanner igjen."
                : "Ingen filer i karantene å prøve på nytt."));
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

public sealed record HotFolderRetryResult(
    int FilesMoved,
    string Message);
