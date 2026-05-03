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

        // Logg til data_imports — utleder periode fra samples min/max time.
        // SCADA-eksporter er ikke nødvendigvis månedlige: de er ofte snapshots
        // av siste N timer/dager. Coverage beregnes mot det faktiske data-
        // spennet (min→max), ikke mot hele måneden — slik at en komplett
        // SCADA-fil for én dag rapporteres som 100 % uavhengig av om dataen
        // dekker resten av måneden.
        //
        // Periode-grensene rundes til måneds-grenser for konsistent matrise-
        // binning — alle samples i samme måned grupperes som én celle.
        if (result.Samples.Count > 0)
        {
            var minTime = result.Samples.Min(s => s.TimeUtc);
            var maxTime = result.Samples.Max(s => s.TimeUtc);

            // Coverage: unike timer / antall timer i data-spennet (NB: ikke måned)
            var minHourly = new DateTimeOffset(minTime.Year, minTime.Month, minTime.Day,
                minTime.Hour, 0, 0, TimeSpan.Zero);
            var maxHourly = new DateTimeOffset(maxTime.Year, maxTime.Month, maxTime.Day,
                maxTime.Hour, 0, 0, TimeSpan.Zero);
            var actualSpanHours = (int)((maxHourly - minHourly).TotalHours) + 1;
            var uniqueHours = result.Samples.Select(s => s.TimeUtc).Distinct().Count();
            var coverage = actualSpanHours > 0
                ? Math.Min(1.0, uniqueHours / (double)actualSpanHours)
                : 1.0;

            // Måneds-binning for matrisen
            var periodFrom = new DateTimeOffset(minTime.Year, minTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var periodTo = periodFrom.AddMonths(1);
            if (maxTime >= periodTo)
            {
                periodTo = new DateTimeOffset(maxTime.Year, maxTime.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
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

        // Logg til data_imports. Operlog dekker varierende perioder — vi
        // henter periode fra event-tidspunkter. Hvis fila er tom, hopper
        // vi over loggingen (ingen meningsfull periode å rapportere).
        if (result.Events.Count > 0)
        {
            await LogOperlogImportAsync(plantId, result.Events, ct).ConfigureAwait(false);
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

        var perPlant = new List<PlantOperlogResult>(result.EventsByPlantId.Count);
        foreach (var (plantId, events) in result.EventsByPlantId)
        {
            await _eventRepo.UpsertManyAsync(events, ct).ConfigureAwait(false);
            perPlant.Add(new PlantOperlogResult(plantId, events.Count));

            if (events.Count > 0)
            {
                await LogOperlogImportAsync(plantId, events, ct).ConfigureAwait(false);
            }
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
    /// utledes fra event-tidsstemplene (rundet til kalender-måneds-grenser).
    /// Coverage settes alltid til 1.0 for operlog fordi fila per definisjon
    /// inneholder kun events som faktisk skjedde — det finnes ingen
    /// "forventet antall events" å normalisere mot.
    /// </summary>
    private async Task LogOperlogImportAsync(
        string plantId,
        IReadOnlyCollection<KraftverkUptime.Core.Domain.ClassifiedEvent> events,
        CancellationToken ct)
    {
        var minTime = events.Min(e => e.StartUtc);
        var maxTime = events.Max(e => e.StartUtc);
        var periodFrom = new DateTimeOffset(minTime.Year, minTime.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodTo = new DateTimeOffset(maxTime.Year, maxTime.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);

        try
        {
            await _dataImportLogger.LogAsync(new DataImportLogEntry(
                PlantId: plantId,
                SourceType: "operlog",
                PeriodFromUtc: periodFrom,
                PeriodToUtc: periodTo,
                FileName: null,
                FileHash: null,
                RowsImported: events.Count,
                CoveragePct: 1.0,
                UserId: "system",
                Notes: $"{events.Count} events"
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
