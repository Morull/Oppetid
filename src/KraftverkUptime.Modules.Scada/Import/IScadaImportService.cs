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

    /// <summary>
    /// Import av 15-min SCADA-eksport. Identisk parse-pipeline som
    /// <see cref="ImportMasterCsvAsync"/>, men skriver til
    /// <c>core.sample_facts_fine</c> via
    /// <see cref="KraftverkUptime.Modules.Scada.Repositories.IScadaSampleFineRepository"/>
    /// slik at hourly-pipelinen ikke overskrives på :00-tidsstempler.
    /// Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md.
    /// </summary>
    Task<ScadaImportResult> ImportMasterCsvFineAsync(
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

    /// <summary>
    /// Multi-anlegg master-CSV-import: én CSV med tags fra flere anlegg
    /// (eks. samlet eksport av Vikeså, Stølskraft, Ørsdalen, Øgreyfoss, Løgjen).
    /// Hver signal-kolonne ruter til riktig plant via prefix-map. Resultatet
    /// blir én <c>data_imports</c>-rad per anlegg som har samples i fila.
    /// Signaler med ukjent prefiks skippes og rapporteres i
    /// <see cref="MultiPlantScadaImportResult.UnknownSignals"/>.
    /// </summary>
    Task<MultiPlantScadaImportResult> ImportMasterCsvMultiPlantAsync(
        string ownerOrgId,
        Stream csvStream,
        CancellationToken ct);

    /// <summary>
    /// 15-min-variant av <see cref="ImportMasterCsvMultiPlantAsync"/>. Samme
    /// signal-prefiks-routing per plant, men skriver til
    /// <c>core.sample_facts_fine</c>. Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md.
    /// </summary>
    Task<MultiPlantScadaImportResult> ImportMasterCsvMultiPlantFineAsync(
        string ownerOrgId,
        Stream csvStream,
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

/// <summary>
/// Resultat fra multi-anlegg master-CSV-import. Inneholder per-plant-statistikk
/// pluss en liste over ukjente signal-prefikser som ble skippet.
/// </summary>
public sealed record MultiPlantScadaImportResult(
    int TotalRowsParsed,
    int TotalRowsSkipped,
    IReadOnlyList<string> UnknownSignals,
    IReadOnlyList<PlantScadaImportResult> PerPlant);

public sealed record PlantScadaImportResult(
    string PlantId,
    int SignalCount,
    int SamplesWritten);
