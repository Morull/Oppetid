namespace KraftverkUptime.Modules.Scada.Import;

/// <summary>
/// Orchestrerer parse + persist for SCADA master-CSV og operlog-CSV.
/// Endepunkter (HTTP) tar imot fil-stream og delegerer hit; selve
/// parsingen er ren funksjon i <see cref="ScadaMasterCsvParser"/> og
/// <see cref="OperlogCsvParser"/>.
/// </summary>
public interface IScadaImportService
{
    Task<ScadaImportResult> ImportMasterCsvAsync(
        string plantId,
        string ownerOrgId,
        Stream csvStream,
        CancellationToken ct);

    Task<OperlogImportResult> ImportOperlogCsvAsync(
        string plantId,
        string ownerOrgId,
        Stream csvStream,
        CancellationToken ct);

    /// <summary>
    /// Multi-anlegg operlog-import: én CSV med events fra flere stasjoner.
    /// Caller leverer en station-til-plantId-callback (typisk DB-lookup på
    /// kanonisk plant-name → slug). Rader for ukjente stasjoner skippes og
    /// telles i <see cref="MultiPlantOperlogImportResult.UnknownStations"/>.
    /// </summary>
    Task<MultiPlantOperlogImportResult> ImportOperlogMultiPlantAsync(
        string ownerOrgId,
        Stream csvStream,
        Func<string, string?> stationToPlantId,
        CancellationToken ct);
}

public sealed record ScadaImportResult(
    string PlantId,
    int SignalCount,
    int RowsParsed,
    int RowsSkipped,
    int SamplesWritten);

public sealed record OperlogImportResult(
    string PlantId,
    int RowsParsed,
    int RowsSkipped,
    int EventsWritten);

/// <summary>
/// Resultat fra multi-anlegg operlog-import. Inneholder per-plant-statistikk
/// pluss aggregerte totaler og en liste over ukjente stasjons-navn (som UI
/// kan vise som advarsel).
/// </summary>
public sealed record MultiPlantOperlogImportResult(
    int TotalRowsParsed,
    int TotalRowsSkipped,
    int UnknownStations,
    IReadOnlyList<string> UnknownStationNames,
    IReadOnlyList<PlantOperlogResult> PerPlant);

public sealed record PlantOperlogResult(
    string PlantId,
    int EventsImported);
