using System.Text.Json;
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

        group.MapPost("/reimport-done", ReimportDoneAsync)
            .WithName("HotFolderReimportDone")
            .WithSummary("Recovery-knapp: flytt alle filer fra done/<YYYY-MM> tilbake til root og slett dedup-cache for ny import. Brukes etter restart der blob-storage er nullstilt.")
            .RequireAuthorization(AuthorizationPolicies.PlantAdmin)
            .Produces<HotFolderRetryResult>(StatusCodes.Status200OK);

        group.MapGet("/quarantine/{fileName}/diagnose", DiagnoseQuarantineAsync)
            .WithName("HotFolderDiagnoseQuarantine")
            .WithSummary("Hent steg-for-steg-trase for hvorfor en karantenert fil ble avvist.")
            .RequireAuthorization(AuthorizationPolicies.PlantReader)
            .Produces<HotFolderDiagnoseResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    /// <summary>
    /// Returnerer diag.json-innholdet (DetectionDiagnostics) for en fil i karantene.
    /// fileName slås opp i quarantine-undermapper (siste 30 dager). Hvis filen er
    /// uten diag.json (eldre filer eller import-feil), returneres en minimal stub
    /// basert på filattributter + .error.txt-innholdet.
    /// </summary>
    private static async Task<IResult> DiagnoseQuarantineAsync(
        string fileName,
        HotFolderOptions options,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.Contains('/', StringComparison.Ordinal)
            || fileName.Contains('\\', StringComparison.Ordinal))
        {
            return Results.Problem(title: "Ugyldig filnavn",
                detail: "fileName må være et bart filnavn uten path-segmenter.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var quarantineRoot = Path.Combine(options.RootPath, options.QuarantineFolderName);
        if (!Directory.Exists(quarantineRoot))
        {
            return Results.Problem(title: "Ingen karantene-mappe",
                statusCode: StatusCodes.Status404NotFound);
        }

        // Søk gjennom alle dag-mapper for fila
        string? filePath = null;
        foreach (var dir in Directory.EnumerateDirectories(quarantineRoot))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                filePath = candidate;
                break;
            }
        }

        if (filePath is null)
        {
            return Results.Problem(title: "Fil ikke i karantene",
                detail: $"Fant ikke '{fileName}' under {quarantineRoot}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var diagPath = filePath + ".diag.json";
        var errorPath = filePath + ".error.txt";

        DetectionDiagnostics? diagnostics = null;
        if (File.Exists(diagPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(diagPath, ct).ConfigureAwait(false);
                diagnostics = JsonSerializer.Deserialize<DetectionDiagnostics>(json);
            }
            catch
            {
                // Korrupt JSON — vi viser stub i stedet for å feile hele kallet
            }
        }

        var errorMessage = File.Exists(errorPath)
            ? await File.ReadAllTextAsync(errorPath, ct).ConfigureAwait(false)
            : null;

        var info = new FileInfo(filePath);
        return Results.Ok(new HotFolderDiagnoseResult(
            FileName: fileName,
            FileSizeBytes: info.Length,
            QuarantinedAtUtc: info.LastWriteTimeUtc,
            ErrorMessage: errorMessage,
            HasDetailedDiagnostics: diagnostics is not null,
            Diagnostics: diagnostics));
    }

    private static async Task<IResult> ReimportDoneAsync(
        HotFolderOptions options,
        HotFolderDedupCache dedup,
        CancellationToken ct)
    {
        if (!options.Enabled)
        {
            return Results.Ok(new HotFolderRetryResult(0, "Hot-folder deaktivert."));
        }

        var doneRoot = Path.Combine(options.RootPath, options.DoneFolderName);
        if (!Directory.Exists(doneRoot))
        {
            return Results.Ok(new HotFolderRetryResult(0, "Ingen done-mappe."));
        }

        // Steg 1: nullstill dedup-cache så filene IKKE klassifiseres som duplikater.
        // Dette er trygt fordi DB-laget har sin egen idempotens-vakt på (file_hash,
        // plant_id) som forhindrer dobbel-import selv om vi prosesserer samme fil.
        dedup.Clear();

        // Steg 2: flytt alle import-bare filer fra done/-mappen tilbake til root.
        // Filer i done/ har stamped prefix (eks. "drivdal_scada_20260503T...") —
        // det er greit, watcher ignorerer prefiks og detekterer på nytt.
        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(doneRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is not (".xlsx" or ".xls" or ".csv")) continue;

            var dest = Path.Combine(options.RootPath, Path.GetFileName(file));
            try
            {
                if (File.Exists(dest))
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var newName = $"{stem}_reimport{DateTime.UtcNow:HHmmss}{ext}";
                    dest = Path.Combine(options.RootPath, newName);
                }
                File.Move(file, dest);
                moved++;
            }
            catch
            {
                // Fortsett med resten — neste kall fanger eventuelle gjenværende
            }
        }

        var watcher = HotFolderWatcher.Current;
        if (watcher is not null && moved > 0)
        {
            await watcher.TriggerScanAsync(ct).ConfigureAwait(false);
        }

        return Results.Ok(new HotFolderRetryResult(moved,
            moved > 0
                ? $"{moved} filer flyttet fra done/ til import-rot, dedup-cache nullstilt. Auto-import scanner nå."
                : "Ingen filer i done/ å re-importere."));
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

public sealed record HotFolderDiagnoseResult(
    string FileName,
    long FileSizeBytes,
    DateTimeOffset QuarantinedAtUtc,
    string? ErrorMessage,
    bool HasDetailedDiagnostics,
    DetectionDiagnostics? Diagnostics);
