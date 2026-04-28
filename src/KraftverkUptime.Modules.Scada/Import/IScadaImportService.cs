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
