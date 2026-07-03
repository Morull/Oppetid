using System.Text;
using KraftverkUptime.Core.DataCompleteness;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Scada.Import;
using KraftverkUptime.Modules.Scada.Repositories;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Scada;

/// <summary>
/// Default-implementasjon av <see cref="IScadaImportService"/>. Lever i
/// Infrastructure fordi den binder seg til EF-repositoriene.
///
/// Tidssone-håndtering: master-CSV inneholder lokale anlegg-tidspunkter uten
/// offset. Foreløpig hardkodes Europe/Oslo. På sikt hentes tz fra
/// <c>core.plants.time_zone</c> per anlegg (allerede et felt i DB).
/// </summary>
public sealed class ScadaImportService : IScadaImportService
{
    private readonly IScadaSampleRepository _sampleRepo;
    private readonly IScadaSampleFineRepository _sampleFineRepo;
    private readonly IClassifiedEventRepository _eventRepo;
    private readonly ISignalMapRepository _signalMapRepo;
    private readonly IDataImportLogger _dataImportLogger;
    private readonly ILogger<ScadaImportService> _logger;

    private static readonly TimeZoneInfo DefaultPlantTimeZone =
        TryFindTz("Europe/Oslo") ?? TryFindTz("W. Europe Standard Time") ?? TimeZoneInfo.Utc;

    public ScadaImportService(
        IScadaSampleRepository sampleRepo,
        IScadaSampleFineRepository sampleFineRepo,
        IClassifiedEventRepository eventRepo,
        ISignalMapRepository signalMapRepo,
        IDataImportLogger dataImportLogger,
        ILogger<ScadaImportService> logger)
    {
        _sampleRepo = sampleRepo;
        _sampleFineRepo = sampleFineRepo;
        _eventRepo = eventRepo;
        _signalMapRepo = signalMapRepo;
        _dataImportLogger = dataImportLogger;
        _logger = logger;
    }

    /// <summary>
    /// Tag-minimering (SPEC-IMPORT-KONSOLIDERT-15MIN Endring D): dropp samples
    /// for signaler som ikke har <c>StoreSamples=true</c> i signal_map — både
    /// kjente-men-deaktiverte og helt ukjente tags. Slik fyller ikke en
    /// overgangsperiode med gamle brede eksporter (481 tags) databasen.
    /// Defensivt: et anlegg HELT uten signal_map-rader filtreres ikke (ellers
    /// ville et useedet anlegg miste all data); dropp telles for notes.
    /// </summary>
    private async Task<(IReadOnlyList<ScadaSample> Kept, int DroppedSamples, int DroppedTags)>
        FilterStorableSamplesAsync(string plantId, IReadOnlyList<ScadaSample> samples, CancellationToken ct)
    {
        if (samples.Count == 0) return (samples, 0, 0);

        IReadOnlyList<KraftverkUptime.Core.Domain.SignalMap> maps;
        try
        {
            maps = await _signalMapRepo.ListForPlantAsync(plantId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "signal_map-oppslag feilet for {PlantId} — importerer uten tag-filter.", plantId);
            return (samples, 0, 0);
        }
        if (maps.Count == 0) return (samples, 0, 0);

        var allowed = maps
            .Where(m => m.StoreSamples)
            .Select(m => m.SignalId)
            .ToHashSet(StringComparer.Ordinal);

        var kept = new List<ScadaSample>(samples.Count);
        var droppedTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in samples)
        {
            if (allowed.Contains(s.SignalId)) kept.Add(s);
            else droppedTags.Add(s.SignalId);
        }
        return (kept, samples.Count - kept.Count, droppedTags.Count);
    }

    /// <summary>
    /// Avleder time-aggregat fra 15-min-samples (SPEC-IMPORT-KONSOLIDERT-15MIN
    /// Endring B): <c>avg(value)</c> per (signal, UTC-time) over kvarterene i
    /// timen, tidsstempel = timens start (:00) — konsistent med hourly-formatet.
    /// Delvise timer (1–3 kvarter) aggregeres over de tilgjengelige. Timer der
    /// alle kvarter mangler verdi får null + quality=2 (bad), ellers 0 (good).
    /// UTC-buckets → DST-overganger trenger ingen særbehandling (parseren har
    /// alt konvertert til UTC). Skrives via samme idempotente upsert som
    /// hourly-importen, så re-import dobler ingenting.
    /// </summary>
    public static IReadOnlyList<ScadaSample> AggregateFineToHourly(IReadOnlyList<ScadaSample> fineSamples)
    {
        return fineSamples
            .GroupBy(s => (s.AssetId, s.SignalId, Hour: FloorToHourUtc(s.TimeUtc)))
            .Select(g =>
            {
                double sum = 0;
                var count = 0;
                foreach (var s in g)
                {
                    if (s.Value.HasValue) { sum += s.Value.Value; count++; }
                }
                return new ScadaSample(
                    g.Key.AssetId,
                    g.Key.SignalId,
                    g.Key.Hour,
                    count > 0 ? sum / count : null,
                    (short)(count > 0 ? 0 : 2));
            })
            .OrderBy(s => s.TimeUtc)
            .ThenBy(s => s.SignalId, StringComparer.Ordinal)
            .ToList();
    }

    private static DateTimeOffset FloorToHourUtc(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Bulk-skriver samples i 5000-batcher via valgt repo. Returnerer antall skrevet.</summary>
    private async Task<int> BulkWriteAsync(
        IReadOnlyList<ScadaSample> samples, bool fine, CancellationToken ct)
    {
        const int batchSize = 5_000;
        var written = 0;
        for (var i = 0; i < samples.Count; i += batchSize)
        {
            var slice = samples.Skip(i).Take(batchSize).ToList();
            written += fine
                ? await _sampleFineRepo.BulkInsertAsync(slice, ct).ConfigureAwait(false)
                : await _sampleRepo.BulkInsertAsync(slice, ct).ConfigureAwait(false);
        }
        return written;
    }

    public async Task<ScadaImportResult> ImportMasterCsvAsync(
        string plantId,
        string ownerOrgId,
        Stream csvStream,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrgId);
        ArgumentNullException.ThrowIfNull(csvStream);

        // ASP.NET 10 forbyr synkron IO på request-body. Buffer hele CSV-en
        // til minnet (typisk <5 MB for hourly), så kan parseren bruke
        // synkront ReadLine() på MemoryStream uten å blokkere.
        using var buffer = new MemoryStream();
        await csvStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        using var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var parser = new ScadaMasterCsvParser();
        var result = parser.Parse(plantId, reader, DefaultPlantTimeZone);

        // Tag-minimering: kun signaler med StoreSamples=true lagres (Endring D).
        var (kept, droppedSamples, droppedTags) =
            await FilterStorableSamplesAsync(plantId, result.Samples, ct).ConfigureAwait(false);

        // Batch-skriv. Repositoriet er idempotent (overskriver duplikater).
        var written = await BulkWriteAsync(kept, fine: false, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "SCADA-import for {PlantId}: {Signals} signaler, {Parsed} timer, {Skipped} skip, {Written} samples skrevet ({Dropped} droppet).",
            plantId, result.SignalCount, result.RowsParsed, result.RowsSkipped, written, droppedSamples);

        // Logg til data_imports — bruker FAKTISKE data-grenser (min/max) som
        // PeriodFrom/PeriodTo, ikke avrundet til måneds-slutt. Det gir korrekt
        // per-måned-dekning i status-matrisen for filer med offset-perioder
        // (eks. en SCADA-eksport som dekker Jan 15 → Mar 14 vil markere mars
        // som ~45 % delvis i stedet for 100 % komplett).
        //
        // Coverage_pct = unike timer / actualSpanHours (samme som før) — regnet
        // på LAGREDE samples (kept), siden det er dem matrisen skal beskrive.
        if (kept.Count > 0)
        {
            var minTime = kept.Min(s => s.TimeUtc);
            var maxTime = kept.Max(s => s.TimeUtc);

            var minHourly = new DateTimeOffset(minTime.Year, minTime.Month, minTime.Day,
                minTime.Hour, 0, 0, TimeSpan.Zero);
            var maxHourly = new DateTimeOffset(maxTime.Year, maxTime.Month, maxTime.Day,
                maxTime.Hour, 0, 0, TimeSpan.Zero);
            var actualSpanHours = (int)((maxHourly - minHourly).TotalHours) + 1;
            var uniqueHours = kept.Select(s => s.TimeUtc).Distinct().Count();
            var coverage = actualSpanHours > 0
                ? Math.Min(1.0, uniqueHours / (double)actualSpanHours)
                : 1.0;

            // Faktisk data-spenn: minHourly → maxHourly + 1 t (eksklusiv slutt)
            var periodFrom = minHourly;
            var periodTo = maxHourly.AddHours(1);

            // Bygg notes med signaler, timer, skip-telemetri og prefiks-validering.
            // Ukjente prefikser (signal-navn som ikke matcher noen kjente plant-
            // tag-mønstre) flagges så drifts-leder ser hvis SCADA endrer
            // tag-konvensjon for et anlegg.
            var unknownPrefixes = DetectUnknownPrefixes(result.Samples
                .Select(s => s.SignalId)
                .Distinct(StringComparer.Ordinal), plantId);

            var notesBuilder = new System.Text.StringBuilder();
            notesBuilder.Append(result.SignalCount).Append(" signaler, ");
            notesBuilder.Append(uniqueHours).Append(" unike timer i ").Append(actualSpanHours)
                .Append(" t-spenn (").Append(minHourly.ToString("yyyy-MM-dd HH"))
                .Append('–').Append(maxHourly.ToString("yyyy-MM-dd HH")).Append(").");
            if (droppedSamples > 0)
            {
                notesBuilder.Append(' ').Append(droppedSamples)
                    .Append(" samples droppet (").Append(droppedTags)
                    .Append(" deaktiverte/ukjente tags).");
            }
            if (result.RowsSkipped > 0)
            {
                notesBuilder.Append(' ').Append(result.RowsSkipped)
                    .Append(" rader skippet (DST-gap, ugyldig timestamp eller for få kolonner).");
            }
            if (unknownPrefixes.Count > 0)
            {
                notesBuilder.Append(" Ukjente prefikser: ")
                    .Append(string.Join(", ", unknownPrefixes.Take(5)));
                if (unknownPrefixes.Count > 5)
                {
                    notesBuilder.Append(" (+").Append(unknownPrefixes.Count - 5).Append(" til)");
                }
                notesBuilder.Append('.');
                _logger.LogWarning(
                    "SCADA-import for {PlantId} har {Count} ukjente tag-prefikser: {Prefixes}",
                    plantId, unknownPrefixes.Count, string.Join(", ", unknownPrefixes));
            }

            try
            {
                await _dataImportLogger.LogAsync(new DataImportLogEntry(
                    PlantId: plantId,
                    SourceType: "scada",
                    PeriodFromUtc: periodFrom,
                    PeriodToUtc: periodTo,
                    FileName: null,
                    FileHash: null,
                    RowsImported: written,
                    CoveragePct: coverage,
                    UserId: "system",
                    Notes: notesBuilder.ToString()
                ), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "data_imports-logging feilet for SCADA {PlantId}, men selve importen er på plass.",
                    plantId);
            }
        }

        return new ScadaImportResult(
            PlantId: plantId,
            SignalCount: result.SignalCount,
            RowsParsed: result.RowsParsed,
            RowsSkipped: result.RowsSkipped,
            SamplesWritten: written);
    }

    /// <summary>
    /// 15-min-variant av <see cref="ImportMasterCsvAsync"/>. Bruker SAMME
    /// parser (master-CSV-formatet er identisk — bare oppløsningen er ulik),
    /// og skriver til <see cref="IScadaSampleFineRepository"/> + avleder
    /// time-aggregat (avg per UTC-time) til <c>sample_facts</c> slik at alle
    /// hourly-konsumenter (overflow, tilsigsmodell, klassifisering, Vakt-ROI)
    /// fungerer uendret med kun én SCADA-eksportjobb. data_imports logges med
    /// SourceType="scada" — 15-min er samme KILDE som hourly, bare finere
    /// oppløsning (SPEC-IMPORT-KONSOLIDERT-15MIN Endring B+C; avløser
    /// «scada-fine»-kilden fra NESTE-CHAT-EFFEKTIVITET-15MIN.md).
    /// </summary>
    public async Task<ScadaImportResult> ImportMasterCsvFineAsync(
        string plantId,
        string ownerOrgId,
        Stream csvStream,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrgId);
        ArgumentNullException.ThrowIfNull(csvStream);

        using var buffer = new MemoryStream();
        await csvStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        using var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var parser = new ScadaMasterCsvParser();
        var result = parser.Parse(plantId, reader, DefaultPlantTimeZone);

        // Tag-minimering: kun signaler med StoreSamples=true lagres (Endring D).
        var (kept, droppedSamples, droppedTags) =
            await FilterStorableSamplesAsync(plantId, result.Samples, ct).ConfigureAwait(false);

        var written = await BulkWriteAsync(kept, fine: true, ct).ConfigureAwait(false);

        // Avled hourly (Endring B) — samme idempotente upsert som hourly-import.
        var hourly = AggregateFineToHourly(kept);
        var hourlyWritten = await BulkWriteAsync(hourly, fine: false, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "SCADA-fine-import for {PlantId}: {Signals} signaler, {Parsed} rader, {Skipped} skip, "
            + "{Written} samples til sample_facts_fine + {Hourly} time-aggregat ({Dropped} droppet).",
            plantId, result.SignalCount, result.RowsParsed, result.RowsSkipped,
            written, hourlyWritten, droppedSamples);

        if (kept.Count > 0)
        {
            // Faktisk data-spenn — for 15-min teller vi unike kvarter (96/dag).
            // Bruker periodFrom = første kvarter, periodTo = siste kvarter + 15 min.
            var minTime = kept.Min(s => s.TimeUtc);
            var maxTime = kept.Max(s => s.TimeUtc);
            var span = maxTime - minTime;
            var totalQuarters = (int)(span.TotalMinutes / 15) + 1;
            var uniqueStamps = kept.Select(s => s.TimeUtc).Distinct().Count();
            var coverage = totalQuarters > 0
                ? Math.Min(1.0, uniqueStamps / (double)totalQuarters)
                : 1.0;
            var periodFrom = minTime;
            var periodTo = maxTime.AddMinutes(15);

            var notes = $"{result.SignalCount} signaler (15-min), {uniqueStamps} unike kvarter i "
                + $"{totalQuarters} kvarter-spenn ({minTime:yyyy-MM-dd HH:mm}–{maxTime:yyyy-MM-dd HH:mm}). "
                + $"Hourly avledet: {hourlyWritten} aggregater.";
            if (droppedSamples > 0)
            {
                notes += $" {droppedSamples} samples droppet ({droppedTags} deaktiverte/ukjente tags).";
            }

            try
            {
                await _dataImportLogger.LogAsync(new DataImportLogEntry(
                    PlantId: plantId,
                    SourceType: "scada",
                    PeriodFromUtc: periodFrom,
                    PeriodToUtc: periodTo,
                    FileName: null,
                    FileHash: null,
                    RowsImported: written,
                    CoveragePct: coverage,
                    UserId: "system",
                    Notes: notes
                ), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "data_imports-logging feilet for SCADA-fine {PlantId}, men selve importen er på plass.",
                    plantId);
            }
        }

        return new ScadaImportResult(
            PlantId: plantId,
            SignalCount: result.SignalCount,
            RowsParsed: result.RowsParsed,
            RowsSkipped: result.RowsSkipped,
            SamplesWritten: written);
    }

    public async Task<OperlogImportResult> ImportOperlogCsvAsync(
        string plantId,
        string ownerOrgId,
        Stream csvStream,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrgId);
        ArgumentNullException.ThrowIfNull(csvStream);

        using var buffer = new MemoryStream();
        await csvStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        using var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);
        var parser = new OperlogCsvParser();
        var result = parser.Parse(ownerOrgId, plantId, reader);

        await _eventRepo.UpsertManyAsync(result.Events, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Operlog-import for {PlantId}: {Parsed} events parsed, {Skipped} skip, {Written} skrevet.",
            plantId, result.RowsParsed, result.RowsSkipped, result.Events.Count);

        // Logg til data_imports basert på rå rader — også settpunkt-events
        // som ikke produserer state-changes teller som "data mottatt for plant".
        // RawStats inkluderer alle rader som ble routet til plant-en uavhengig
        // av om de matcher noen state-mønster.
        if (result.RawStats is { } stats)
        {
            await LogOperlogImportAsync(plantId, stats, ct).ConfigureAwait(false);
        }

        return new OperlogImportResult(
            PlantId: plantId,
            RowsParsed: result.RowsParsed,
            RowsSkipped: result.RowsSkipped,
            EventsWritten: result.Events.Count);
    }

    public async Task<MultiPlantOperlogImportResult> ImportOperlogMultiPlantAsync(
        string ownerOrgId,
        Stream csvStream,
        Func<string, string?> stationToPlantId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrgId);
        ArgumentNullException.ThrowIfNull(csvStream);
        ArgumentNullException.ThrowIfNull(stationToPlantId);

        using var buffer = new MemoryStream();
        await csvStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        using var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var parser = new OperlogCsvParser();
        var result = parser.ParseMultiPlant(ownerOrgId, reader, stationToPlantId);

        var perPlant = new List<PlantOperlogResult>(result.RowsByPlantId.Count);
        // Iter over ALLE plants som hadde rader (inkl. settpunkt-only) — ikke
        // bare de med state-classified events. Slik logges grodemfoss-måneden
        // som "data mottatt" selv om ingen events ble klassifisert.
        foreach (var (plantId, stats) in result.RowsByPlantId)
        {
            var events = result.EventsByPlantId.TryGetValue(plantId, out var list)
                ? list
                : Array.Empty<KraftverkUptime.Core.Domain.ClassifiedEvent>();
            if (events.Count > 0)
            {
                await _eventRepo.UpsertManyAsync(events, ct).ConfigureAwait(false);
            }
            perPlant.Add(new PlantOperlogResult(plantId, events.Count));

            await LogOperlogImportAsync(plantId, stats, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Multi-plant operlog-import: {Parsed} events parsed, {Skipped} skip, " +
            "{PlantCount} plants, {Unknown} ukjente stasjoner.",
            result.RowsParsed, result.RowsSkipped, perPlant.Count, result.UnknownStations);

        return new MultiPlantOperlogImportResult(
            TotalRowsParsed: result.RowsParsed,
            TotalRowsSkipped: result.RowsSkipped,
            UnknownStations: result.UnknownStations,
            UnknownStationNames: result.UnknownStationNames,
            PerPlant: perPlant);
    }

    public Task<MultiPlantScadaImportResult> ImportMasterCsvMultiPlantAsync(
        string ownerOrgId,
        Stream csvStream,
        CancellationToken ct)
        => ImportMasterCsvMultiPlantCoreAsync(ownerOrgId, csvStream, isFine: false, ct);

    public Task<MultiPlantScadaImportResult> ImportMasterCsvMultiPlantFineAsync(
        string ownerOrgId,
        Stream csvStream,
        CancellationToken ct)
        => ImportMasterCsvMultiPlantCoreAsync(ownerOrgId, csvStream, isFine: true, ct);

    /// <summary>
    /// Felles multi-plant-import som tar et flag for å velge fine vs hourly
    /// destinasjons-repo og data_imports-SourceType. Holder parse + per-plant-
    /// statistikk-logikken i ett kodeområde.
    /// </summary>
    private async Task<MultiPlantScadaImportResult> ImportMasterCsvMultiPlantCoreAsync(
        string ownerOrgId,
        Stream csvStream,
        bool isFine,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrgId);
        ArgumentNullException.ThrowIfNull(csvStream);

        using var buffer = new MemoryStream();
        await csvStream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        using var reader = new StreamReader(
            buffer,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: true);

        var parser = new ScadaMasterCsvParser();
        var result = parser.ParseMultiPlant(reader, DefaultPlantTimeZone, MapSignalToPlantId);

        var perPlant = new List<PlantScadaImportResult>(result.PerPlant.Count);
        // Én SCADA-kilde (Endring C): 15-min og hourly er samme kilde i
        // completeness-matrisen; notes skiller oppløsningen.
        const string sourceTypeKey = "scada";

        foreach (var plant in result.PerPlant)
        {
            // Tag-minimering per anlegg (Endring D).
            var (kept, droppedSamples, droppedTags) =
                await FilterStorableSamplesAsync(plant.PlantId, plant.Samples, ct).ConfigureAwait(false);

            var written = await BulkWriteAsync(kept, fine: isFine, ct).ConfigureAwait(false);

            // Avled hourly fra 15-min (Endring B).
            var hourlyWritten = 0;
            if (isFine && kept.Count > 0)
            {
                var hourly = AggregateFineToHourly(kept);
                hourlyWritten = await BulkWriteAsync(hourly, fine: false, ct).ConfigureAwait(false);
            }
            perPlant.Add(new PlantScadaImportResult(plant.PlantId, plant.SignalCount, written));

            // Logg data_imports per anlegg basert på faktisk data-spenn.
            if (kept.Count == 0) continue;
            var minTime = kept.Min(s => s.TimeUtc);
            var maxTime = kept.Max(s => s.TimeUtc);

            DateTimeOffset periodFrom, periodTo;
            double coverage;
            int spanUnits;
            int uniqueUnits;
            string unitLabel;

            if (isFine)
            {
                // 15-min: tell unike kvarter, periode = [min, max + 15 min).
                var span = maxTime - minTime;
                spanUnits = (int)(span.TotalMinutes / 15) + 1;
                uniqueUnits = kept.Select(s => s.TimeUtc).Distinct().Count();
                coverage = spanUnits > 0 ? Math.Min(1.0, uniqueUnits / (double)spanUnits) : 1.0;
                periodFrom = minTime;
                periodTo = maxTime.AddMinutes(15);
                unitLabel = "kvarter";
            }
            else
            {
                var minHourly = new DateTimeOffset(minTime.Year, minTime.Month, minTime.Day,
                    minTime.Hour, 0, 0, TimeSpan.Zero);
                var maxHourly = new DateTimeOffset(maxTime.Year, maxTime.Month, maxTime.Day,
                    maxTime.Hour, 0, 0, TimeSpan.Zero);
                spanUnits = (int)((maxHourly - minHourly).TotalHours) + 1;
                uniqueUnits = kept.Select(s => s.TimeUtc).Distinct().Count();
                coverage = spanUnits > 0 ? Math.Min(1.0, uniqueUnits / (double)spanUnits) : 1.0;
                periodFrom = minHourly;
                periodTo = maxHourly.AddHours(1);
                unitLabel = "timer";
            }

            var notes = $"{plant.SignalCount} signaler (multi-plant{(isFine ? ", 15-min" : "")}), "
                + $"{uniqueUnits} unike {unitLabel} i {spanUnits} {unitLabel}-spenn "
                + $"({minTime:yyyy-MM-dd HH:mm}–{maxTime:yyyy-MM-dd HH:mm}).";
            if (isFine)
            {
                notes += $" Hourly avledet: {hourlyWritten} aggregater.";
            }
            if (droppedSamples > 0)
            {
                notes += $" {droppedSamples} samples droppet ({droppedTags} deaktiverte/ukjente tags).";
            }

            try
            {
                await _dataImportLogger.LogAsync(new DataImportLogEntry(
                    PlantId: plant.PlantId,
                    SourceType: sourceTypeKey,
                    PeriodFromUtc: periodFrom,
                    PeriodToUtc: periodTo,
                    FileName: null,
                    FileHash: null,
                    RowsImported: written,
                    CoveragePct: coverage,
                    UserId: "system",
                    Notes: notes
                ), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "data_imports-logging feilet for multi-plant SCADA {PlantId} (isFine={IsFine}).",
                    plant.PlantId, isFine);
            }
        }

        _logger.LogInformation(
            "Multi-plant SCADA-import ({SourceType}): {Parsed} rader, {Skipped} skip, {PlantCount} anlegg, " +
            "{UnknownSignals} ukjente signaler.",
            sourceTypeKey, result.RowsParsed, result.RowsSkipped, perPlant.Count, result.UnmappedSignals.Count);

        return new MultiPlantScadaImportResult(
            TotalRowsParsed: result.RowsParsed,
            TotalRowsSkipped: result.RowsSkipped,
            UnknownSignals: result.UnmappedSignals,
            PerPlant: perPlant);
    }

    /// <summary>
    /// Mapping fra signal-id (eks. "VIKESA_G1_GEN_P_PV") til plant-id (eks. "vikesa").
    /// Bruker prefiks før første underscore + slår opp i <see cref="KnownPrefixesByPlant"/>.
    /// Returnerer null hvis prefiks ikke matcher noe kjent anlegg.
    /// </summary>
    private static string? MapSignalToPlantId(string signalId)
    {
        if (string.IsNullOrEmpty(signalId)) return null;
        var idx = signalId.AsSpan().IndexOfAny(PrefixSeparators);
        var prefix = idx > 0 ? signalId[..idx] : signalId;
        foreach (var (plantId, knownPrefixes) in KnownPrefixesByPlant)
        {
            if (knownPrefixes.Contains(prefix)) return plantId;
        }
        return null;
    }

    /// <summary>
    /// Logger én rad til <c>data_imports</c> for en operlog-batch. Periode
    /// rundes til hele måneds-grenser (måneds-start for første event,
    /// måneds-slutt for siste) fordi operlog er hendelse-basert: hvis
    /// ingen alarmer skjedde 1.-4. april, betyr ikke det at data mangler
    /// for de dagene — det betyr bare fredelig drift. Ved å bruke hele
    /// måneds-spenn unngår vi at completeness-matrisen feilrapporterer
    /// stille perioder som "delvis dekning". Coverage er 1.0 fordi fila
    /// per definisjon inneholder alle events som faktisk skjedde.
    /// </summary>
    private async Task LogOperlogImportAsync(
        string plantId,
        PlantRawRowStats stats,
        CancellationToken ct)
    {
        var periodFrom = new DateTimeOffset(
            stats.MinTimestampUtc.Year, stats.MinTimestampUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var lastMonthStart = new DateTimeOffset(
            stats.MaxTimestampUtc.Year, stats.MaxTimestampUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodTo = lastMonthStart.AddMonths(1);

        try
        {
            await _dataImportLogger.LogAsync(new DataImportLogEntry(
                PlantId: plantId,
                SourceType: "operlog",
                PeriodFromUtc: periodFrom,
                PeriodToUtc: periodTo,
                FileName: null,
                FileHash: null,
                RowsImported: stats.RowCount,
                CoveragePct: 1.0,
                UserId: "system",
                Notes: $"{stats.RowCount} rader"
            ), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "data_imports-logging feilet for operlog {PlantId}, men selve importen er på plass.",
                plantId);
        }
    }

    private static TimeZoneInfo? TryFindTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return null; }
    }

    /// <summary>
    /// Mapping fra plant-id til kjente SCADA-tag-prefiks. Holdes synkronisert med
    /// HotFolderOptions.PlantPrefixMap i Infrastructure/HotFolder. Eksponert som
    /// felles statisk så validering også kan kjøres for SCADA-master-imports som
    /// IKKE går via hot-folder (manuell opplasting).
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> KnownPrefixesByPlant = new(StringComparer.Ordinal)
    {
        ["drivdal"] = new(StringComparer.OrdinalIgnoreCase) { "DRIVDAL", "DRIV" },
        ["lindland"] = new(StringComparer.OrdinalIgnoreCase) { "LINDLAND", "LIND" },
        ["haukland"] = new(StringComparer.OrdinalIgnoreCase) { "HAUKLAND", "HAUK" },
        ["honnefoss"] = new(StringComparer.OrdinalIgnoreCase) { "HONNE", "LIAVT" }, // LIAVT i Honnefoss-eksport tilhører Honnefoss-inntak
        ["liavatn"] = new(StringComparer.OrdinalIgnoreCase) { "LIAVATN" },
        ["grodemfoss"] = new(StringComparer.OrdinalIgnoreCase) { "GRODEM", "GRODEMFOSS" },
        ["ogreyfoss"] = new(StringComparer.OrdinalIgnoreCase) { "OGREY", "OGREYFOSS", "OGREY1", "OGREY2" },
        ["logjen"] = new(StringComparer.OrdinalIgnoreCase) { "LOGJEN", "LOG" },
        ["orsdalen"] = new(StringComparer.OrdinalIgnoreCase) { "ORSDAL", "ORSDALEN" },
        ["vikesa"] = new(StringComparer.OrdinalIgnoreCase) { "VIKESA", "VIKE" },
        ["stolskraft"] = new(StringComparer.OrdinalIgnoreCase) { "STOLS", "STOLSKRAFT" },
    };

    /// <summary>Cached separator-set for prefiks-uttrekk (CA1870).</summary>
    private static readonly System.Buffers.SearchValues<char> PrefixSeparators =
        System.Buffers.SearchValues.Create("_.");

    /// <summary>
    /// Returnerer signal-prefikser i SCADA-importen som IKKE er kjente for det
    /// angitte anlegget. Brukes til å varsle drifts-leder om at SCADA-systemet
    /// kan ha endret tag-konvensjon. Tom liste = alt OK.
    /// </summary>
    private static List<string> DetectUnknownPrefixes(IEnumerable<string> signalIds, string plantId)
    {
        if (!KnownPrefixesByPlant.TryGetValue(plantId, out var known))
        {
            return new(); // Ukjent plant — vi har ikke autoritativ liste, hopp over
        }

        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in signalIds)
        {
            // Hent ut prefiks før første '_' eller '.'
            var idx = signal.AsSpan().IndexOfAny(PrefixSeparators);
            var prefix = idx > 0 ? signal[..idx] : signal;
            if (string.IsNullOrEmpty(prefix)) continue;
            if (!known.Contains(prefix))
            {
                unknown.Add(prefix);
            }
        }
        return unknown.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }
}
