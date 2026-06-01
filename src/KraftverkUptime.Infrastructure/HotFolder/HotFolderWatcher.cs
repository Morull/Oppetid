using System.Text.Json;
using KraftverkUptime.Modules.Scada.Import;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Infrastructure.HotFolder;

/// <summary>
/// BackgroundService som overvåker en mappe for nye .xlsx/.csv-filer og
/// auto-importerer dem (SPEC-AUTO-IMPORT-FOLDER).
///
/// Strategi: polling hvert N sekunder + fil-stabilitets-sjekk for å unngå
/// å lese filer som fortsatt blir kopiert. Filer flyttes til
/// <c>done/&lt;YYYY-MM&gt;/</c> ved suksess, eller <c>quarantine/&lt;YYYY-MM-DD&gt;/</c>
/// ved feil med .error.txt-vedlegg.
///
/// Kjører kun hvis <c>HotFolder:Enabled = true</c> og rot-mappa eksisterer.
/// Hvis <c>HotFolder:ManualOnly = true</c> hopper den over polling-loopen
/// og scanner kun når <see cref="TriggerScanAsync"/> kalles fra API-en.
/// </summary>
public sealed class HotFolderWatcher : BackgroundService
{
    /// <summary>Singleton-instansen for ekstern triggering (manuell scan).</summary>
    public static HotFolderWatcher? Current { get; private set; }

    private readonly IServiceProvider _services;
    private readonly HotFolderQueue _queue;
    private readonly HotFolderDetector _detector;
    private readonly HotFolderDedupCache _dedup;
    private readonly HotFolderOptions _options;
    private readonly ILogger<HotFolderWatcher> _log;

    private readonly Dictionary<string, DateTime> _seenFiles = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions DiagJsonOpts = new()
    {
        WriteIndented = true,
    };

    public HotFolderWatcher(
        IServiceProvider services,
        HotFolderQueue queue,
        HotFolderDetector detector,
        HotFolderDedupCache dedup,
        IOptions<HotFolderOptions> options,
        ILogger<HotFolderWatcher> log)
    {
        _services = services;
        _queue = queue;
        _detector = detector;
        _dedup = dedup;
        _options = options.Value;
        _log = log;
        Current = this;
    }

    /// <summary>
    /// Eksternt-triggert scan (brukes av "Skann nå"-knappen i UI eller
    /// <c>POST /api/v1/hot-folder/scan-now</c>). Trygt å kalle parallelt
    /// med polling-loopen — duplikat-deteksjon i <c>_seenFiles</c> hindrer
    /// dobbel-prosessering.
    /// </summary>
    public Task TriggerScanAsync(CancellationToken ct = default) => ScanOnceAsync(ct);

    /// <summary>Filer som matcher ExcludePatterns ignoreres.</summary>
    private bool IsExcluded(string fileName)
    {
        foreach (var pattern in _options.ExcludePatterns)
        {
            if (fileName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ct = stoppingToken;
        if (!_options.Enabled)
        {
            _log.LogInformation("HotFolder disabled — skipper watcher.");
            return;
        }

        var rootPath = _options.RootPath;
        if (!Directory.Exists(rootPath))
        {
            _log.LogWarning("HotFolder rot-mappe finnes ikke: {Root}. Watcher kjører uten å gjøre noe.", rootPath);
            return;
        }

        if (_options.ManualOnly)
        {
            _log.LogInformation(
                "HotFolder watcher i manuell modus. Klar til å scanne {Root} på etterspørsel " +
                "(POST /api/v1/hot-folder/scan-now).", rootPath);
            // Hold tjenesten i live så TriggerScanAsync kan kalles.
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { }
            return;
        }

        _log.LogInformation("HotFolder watcher startet. Overvåker {Root} hvert {Sec} sekund.",
            rootPath, _options.PollIntervalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HotFolder scan feilet.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task ScanOnceAsync(CancellationToken ct)
    {
        var root = new DirectoryInfo(_options.RootPath);
        if (!root.Exists) return;

        // Bare topp-nivå (ikke done/, quarantine/, processing/-undermapper)
        var files = root.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
            .Where(f =>
                f.Extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
                || f.Extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
                || f.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
            .Where(f => !IsExcluded(f.Name))
            .ToList();

        if (files.Count == 0) return;

        var stabilityWait = TimeSpan.FromSeconds(_options.FileStabilitySeconds);
        var now = DateTime.UtcNow;

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) return;

            // Fil-stabilitets-sjekk: vent til last-write-time har vært
            // uendret i FileStabilitySeconds. Hindrer at vi prosesserer
            // en fil mens den fortsatt blir kopiert.
            if (now - file.LastWriteTimeUtc < stabilityWait)
            {
                _queue.EnqueueDetected(file.FullName, file.Length, file.LastWriteTimeUtc);
                continue;
            }

            // Idempotens — ikke prosesser samme fil flere ganger basert på
            // last-write-time + path.
            if (_seenFiles.TryGetValue(file.FullName, out var lastSeen)
                && lastSeen >= file.LastWriteTimeUtc)
            {
                continue;
            }
            _seenFiles[file.FullName] = file.LastWriteTimeUtc;

            _queue.EnqueueDetected(file.FullName, file.Length, file.LastWriteTimeUtc);
            await ProcessFileAsync(file, ct).ConfigureAwait(false);
        }
    }

    private async Task ProcessFileAsync(FileInfo file, CancellationToken ct)
    {
        _queue.MarkProcessing(file.FullName);
        _log.LogInformation("HotFolder: prosesserer {File}", file.Name);

        // 1. Hash innholdet før noe annet — dedup-vakt mot at samme fil kommer
        //    inn flere ganger (drag-drop x2, "Skann nå" trykket gjentatte ganger,
        //    container-restart med fil fortsatt liggende).
        string fileHash;
        try
        {
            fileHash = await HotFolderDedupCache.ComputeFileHashAsync(file, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HotFolder: kunne ikke hashe {File}", file.Name);
            await QuarantineAsync(file, $"Kunne ikke lese fil: {ex.Message}", diagnostics: null, ct);
            _queue.Complete(file.FullName, "QUARANTINE", null, null,
                $"Kunne ikke lese fil: {ex.Message}", DateTimeOffset.UtcNow);
            return;
        }

        // 2. Detect type + plant (bygger diagnostics underveis)
        var detection = _detector.Detect(file);
        var diag = detection.Diagnostics;

        if (!detection.Success)
        {
            await QuarantineAsync(file, detection.ErrorMessage ?? "Ukjent fil-type/anlegg", diag, ct);
            _queue.Complete(file.FullName, "QUARANTINE", null, null,
                detection.ErrorMessage, DateTimeOffset.UtcNow);
            return;
        }

        // 3. Dedup-sjekk: registrer hash. Hvis allerede sett → flytt til duplicates/.
        //    Sjekken kjøres ETTER detect slik at duplicates-loggen får plant-info.
        if (!_dedup.TryRegister(fileHash, file.Name,
                detection.PlantId, detection.SourceType?.ToSourceTypeKey(), out var existing))
        {
            _log.LogInformation(
                "HotFolder: duplikat av {OrigFile} (hash {Hash}, sett første gang {Time}) — hopper over.",
                existing.FirstFileName, fileHash[..12], existing.FirstSeenUtc);
            await MoveToDuplicatesAsync(file, existing, ct);
            _queue.Complete(file.FullName, "DUPLICATE",
                detection.PlantId, detection.SourceType?.ToSourceTypeKey(),
                $"Duplikat av '{existing.FirstFileName}' (importert {existing.FirstSeenUtc:dd.MM HH:mm}).",
                DateTimeOffset.UtcNow);
            return;
        }

        // 4. Rute til riktig importør via DI scope
        try
        {
            using var scope = _services.CreateScope();
            await RouteAndImportAsync(scope, file, detection.PlantId!, detection.SourceType!.Value, ct);

            await MoveToDoneAsync(file, detection.PlantId!, detection.SourceType.Value.ToSourceTypeKey(), ct);
            _queue.Complete(file.FullName, "OK",
                detection.PlantId, detection.SourceType.Value.ToSourceTypeKey(),
                "Auto-import ok", DateTimeOffset.UtcNow);
            _log.LogInformation("HotFolder OK: {File} → {Plant}/{Source}",
                file.Name, detection.PlantId, detection.SourceType);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HotFolder import feilet for {File}", file.Name);
            await QuarantineAsync(file, ex.ToString(), diag, ct);
            _queue.Complete(file.FullName, "QUARANTINE",
                detection.PlantId, detection.SourceType?.ToSourceTypeKey(),
                $"Feil: {ex.Message}", DateTimeOffset.UtcNow);
        }
    }

    private static async Task RouteAndImportAsync(
        IServiceScope scope, FileInfo file, string plantId, SourceType type, CancellationToken ct)
    {
        await using var stream = file.OpenRead();

        switch (type)
        {
            case SourceType.Settlement:
                {
                    var httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
                        .CreateClient("HotFolderUpload");
                    using var content = new MultipartFormDataContent();
                    using var fileContent = new StreamContent(stream);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                    content.Add(fileContent, "file", file.Name);

                    var requestUri = new Uri(
                        $"api/v1/plants/{Uri.EscapeDataString(plantId)}/settlements", UriKind.Relative);
                    var resp = await httpClient.PostAsync(requestUri, content, ct);
                    resp.EnsureSuccessStatusCode();
                    break;
                }
            case SourceType.SettlementMultiPlant:
                {
                    // Multi-plant xlsx: rute til /settlements/multi-plant. Endepunktet
                    // splitter automatisk på plant-faner og oppretter én import per anlegg.
                    var httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
                        .CreateClient("HotFolderUpload");
                    using var content = new MultipartFormDataContent();
                    using var fileContent = new StreamContent(stream);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                    content.Add(fileContent, "file", file.Name);

                    var requestUri = new Uri(
                        "api/v1/settlements/multi-plant", UriKind.Relative);
                    var resp = await httpClient.PostAsync(requestUri, content, ct);
                    resp.EnsureSuccessStatusCode();
                    break;
                }
            case SourceType.ScadaTrends:
                {
                    var scadaSvc = scope.ServiceProvider.GetRequiredService<IScadaImportService>();
                    var ownerOrg = "dev-org"; // TODO: hent fra plant-config når multi-tenant aktiveres
                    await scadaSvc.ImportMasterCsvAsync(plantId, ownerOrg, stream, ct);
                    break;
                }
            case SourceType.ScadaTrendsFine:
                {
                    // 15-min-eksport — separat pipeline mot sample_facts_fine. Spec
                    // NESTE-CHAT-EFFEKTIVITET-15MIN.md.
                    var scadaSvc = scope.ServiceProvider.GetRequiredService<IScadaImportService>();
                    var ownerOrg = "dev-org";
                    await scadaSvc.ImportMasterCsvFineAsync(plantId, ownerOrg, stream, ct);
                    break;
                }
            case SourceType.ScadaAlarms:
                {
                    var scadaSvc = scope.ServiceProvider.GetRequiredService<IScadaImportService>();
                    var ownerOrg = "dev-org";
                    await scadaSvc.ImportOperlogCsvAsync(plantId, ownerOrg, stream, ct);
                    break;
                }
            case SourceType.ScadaAlarmsMultiPlant:
                {
                    // Multi-plant operlog: ruter til /api/v1/operlog/multi-plant
                    // som splitter på station-feltet og oppretter en data_imports-
                    // rad per anlegg. Samme flyt som drag-drop på /data-import.
                    var httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
                        .CreateClient("HotFolderUpload");
                    using var content = new MultipartFormDataContent();
                    using var fileContent = new StreamContent(stream);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
                    content.Add(fileContent, "file", file.Name);
                    var requestUri = new Uri("api/v1/operlog/multi-plant", UriKind.Relative);
                    var resp = await httpClient.PostAsync(requestUri, content, ct);
                    resp.EnsureSuccessStatusCode();
                    break;
                }
            case SourceType.ScadaTrendsMultiPlant:
                {
                    // Multi-plant SCADA master-CSV: ruter til /api/v1/scada/multi-plant
                    // som splitter per signal-prefiks og oppretter én data_imports-
                    // rad per anlegg. Brukes for samlet eksport av Vikeså,
                    // Stølskraft, Ørsdalen, Øgreyfoss, Løgjen i én fil.
                    var httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
                        .CreateClient("HotFolderUpload");
                    using var content = new MultipartFormDataContent();
                    using var fileContent = new StreamContent(stream);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
                    content.Add(fileContent, "file", file.Name);
                    var requestUri = new Uri("api/v1/scada/multi-plant", UriKind.Relative);
                    var resp = await httpClient.PostAsync(requestUri, content, ct);
                    resp.EnsureSuccessStatusCode();
                    break;
                }
            case SourceType.ScadaTrendsFineMultiPlant:
                {
                    // 15-min multi-plant: samme prefiks-routing, men skriver til
                    // sample_facts_fine. Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md.
                    var httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
                        .CreateClient("HotFolderUpload");
                    using var content = new MultipartFormDataContent();
                    using var fileContent = new StreamContent(stream);
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
                    content.Add(fileContent, "file", file.Name);
                    var requestUri = new Uri("api/v1/scada/multi-plant-fine", UriKind.Relative);
                    var resp = await httpClient.PostAsync(requestUri, content, ct);
                    resp.EnsureSuccessStatusCode();
                    break;
                }
        }
    }

    private async Task MoveToDoneAsync(FileInfo file, string plantId, string sourceKey, CancellationToken ct)
    {
        var doneRoot = Path.Combine(_options.RootPath, _options.DoneFolderName);
        var monthBucket = DateTime.UtcNow.ToString("yyyy-MM");
        var targetDir = Path.Combine(doneRoot, monthBucket);
        Directory.CreateDirectory(targetDir);

        var stamped = HotFolderNaming.BuildStampedName(
            $"{plantId}_{sourceKey}_{DateTime.UtcNow:yyyyMMddTHHmmssfff}_", file.Name);
        var targetPath = Path.Combine(targetDir, stamped);
        File.Move(file.FullName, targetPath, overwrite: false);
        await Task.CompletedTask;
    }

    private async Task QuarantineAsync(FileInfo file, string errorMessage,
        DetectionDiagnostics? diagnostics, CancellationToken ct)
    {
        try
        {
            var quarantineRoot = Path.Combine(_options.RootPath, _options.QuarantineFolderName);
            var dayBucket = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var targetDir = Path.Combine(quarantineRoot, dayBucket);
            Directory.CreateDirectory(targetDir);

            // Strip akkumulerte stamp-prefikser + kapp lengde slik at heller ikke
            // karantene-flyttingen feiler på MAX_PATH (.error.txt/.diag.json legger
            // på ekstra tegn, derfor samme budsjett som done/duplicates).
            var safeName = HotFolderNaming.BuildStampedName(string.Empty, file.Name);
            var targetPath = Path.Combine(targetDir, safeName);
            // Hvis fil med samme navn finnes, legg til timestamp-suffiks
            if (File.Exists(targetPath))
            {
                var stem = Path.GetFileNameWithoutExtension(safeName);
                var ext = Path.GetExtension(safeName);
                targetPath = Path.Combine(targetDir, $"{stem}_{DateTime.UtcNow:HHmmss}{ext}");
            }
            File.Move(file.FullName, targetPath);
            await File.WriteAllTextAsync(targetPath + ".error.txt", errorMessage, ct);

            // Skriv diag.json hvis vi har detector-trase. UI henter denne via
            // /api/v1/hot-folder/quarantine/{fileName}/diagnose for å vise
            // "hvorfor havnet denne i karantene"-modal.
            if (diagnostics is not null)
            {
                try
                {
                    var diagJson = JsonSerializer.Serialize(diagnostics, DiagJsonOpts);
                    await File.WriteAllTextAsync(targetPath + ".diag.json", diagJson, ct);
                }
                catch (Exception diagEx)
                {
                    _log.LogWarning(diagEx, "Kunne ikke skrive .diag.json for {File} — fortsetter uten.", file.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Klarte ikke flytte til quarantine: {File}", file.Name);
        }
    }

    private async Task MoveToDuplicatesAsync(FileInfo file, DedupRecord existing, CancellationToken ct)
    {
        try
        {
            var dupRoot = Path.Combine(_options.RootPath, _options.DuplicatesFolderName);
            var monthBucket = DateTime.UtcNow.ToString("yyyy-MM");
            var targetDir = Path.Combine(dupRoot, monthBucket);
            Directory.CreateDirectory(targetDir);

            var stamped = HotFolderNaming.BuildStampedName(
                $"dup_{DateTime.UtcNow:yyyyMMddTHHmmssfff}_", file.Name);
            var targetPath = Path.Combine(targetDir, stamped);
            File.Move(file.FullName, targetPath, overwrite: false);

            var note =
                $"Duplikat av tidligere import.\n" +
                $"Hash: {existing.Hash}\n" +
                $"Først sett: {existing.FirstSeenUtc:O}\n" +
                $"Originalt filnavn: {existing.FirstFileName}\n" +
                $"Plant: {existing.PlantId ?? "(ukjent)"}\n" +
                $"Kilde: {existing.SourceType ?? "(ukjent)"}\n";
            await File.WriteAllTextAsync(targetPath + ".dup.txt", note, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Klarte ikke flytte til duplicates: {File}", file.Name);
        }
    }
}
