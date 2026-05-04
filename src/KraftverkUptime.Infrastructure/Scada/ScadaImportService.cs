using System.Text;
using KraftverkUptime.Core.DataCompleteness;
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
    private readonly IClassifiedEventRepository _eventRepo;
    private readonly IDataImportLogger _dataImportLogger;
    private readonly ILogger<ScadaImportService> _logger;

    private static readonly TimeZoneInfo DefaultPlantTimeZone =
        TryFindTz("Europe/Oslo") ?? TryFindTz("W. Europe Standard Time") ?? TimeZoneInfo.Utc;

    public ScadaImportService(
        IScadaSampleRepository sampleRepo,
        IClassifiedEventRepository eventRepo,
        IDataImportLogger dataImportLogger,
        ILogger<ScadaImportService> logger)
    {
        _sampleRepo = sampleRepo;
        _eventRepo = eventRepo;
        _dataImportLogger = dataImportLogger;
        _logger = logger;
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

        // Batch-skriv. Repositoriet er idempotent (overskriver duplikater).
        const int batchSize = 5_000;
        var written = 0;
        for (var i = 0; i < result.Samples.Count; i += batchSize)
        {
            var slice = result.Samples
                .Skip(i)
                .Take(batchSize)
                .ToList();
            written += await _sampleRepo.BulkInsertAsync(slice, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "SCADA-import for {PlantId}: {Signals} signaler, {Parsed} timer, {Skipped} skip, {Written} samples skrevet.",
            plantId, result.SignalCount, result.RowsParsed, result.RowsSkipped, written);

        // Logg til data_imports — bruker FAKTISKE data-grenser (min/max) som
        // PeriodFrom/PeriodTo, ikke avrundet til måneds-slutt. Det gir korrekt
        // per-måned-dekning i status-matrisen for filer med offset-perioder
        // (eks. en SCADA-eksport som dekker Jan 15 → Mar 14 vil markere mars
        // som ~45 % delvis i stedet for 100 % komplett).
        //
        // Coverage_pct = unike timer / actualSpanHours (samme som før).
        if (result.Samples.Count > 0)
        {
            var minTime = result.Samples.Min(s => s.TimeUtc);
            var maxTime = result.Samples.Max(s => s.TimeUtc);

            var minHourly = new DateTimeOffset(minTime.Year, minTime.Month, minTime.Day,
                minTime.Hour, 0, 0, TimeSpan.Zero);
            var maxHourly = new DateTimeOffset(maxTime.Year, maxTime.Month, maxTime.Day,
                maxTime.Hour, 0, 0, TimeSpan.Zero);
            var actualSpanHours = (int)((maxHourly - minHourly).TotalHours) + 1;
            var uniqueHours = result.Samples.Select(s => s.TimeUtc).Distinct().Count();
            var coverage = actualSpanHours > 0
                ? Math.Min(1.0, uniqueHours / (double)actualSpanHours)
                : 1.0;

            // Faktisk data-spenn: minHourly → maxHourly + 1 t (eksklusiv slutt)
            var periodFrom = minHourly;
            var periodTo = maxHourly.AddHours(1);

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
                    Notes: $"{result.SignalCount} signaler, {uniqueHours} unike timer i {actualSpanHours} t-spenn ({minHourly:yyyy-MM-dd HH}–{maxHourly:yyyy-MM-dd HH})"
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
}
