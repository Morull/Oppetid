using System.Text;
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
    private readonly ILogger<ScadaImportService> _logger;

    private static readonly TimeZoneInfo DefaultPlantTimeZone =
        TryFindTz("Europe/Oslo") ?? TryFindTz("W. Europe Standard Time") ?? TimeZoneInfo.Utc;

    public ScadaImportService(
        IScadaSampleRepository sampleRepo,
        IClassifiedEventRepository eventRepo,
        ILogger<ScadaImportService> logger)
    {
        _sampleRepo = sampleRepo;
        _eventRepo = eventRepo;
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

    private static TimeZoneInfo? TryFindTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return null; }
    }
}
