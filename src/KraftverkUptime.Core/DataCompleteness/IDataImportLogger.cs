namespace KraftverkUptime.Core.DataCompleteness;

/// <summary>
/// Skriver én rad til <c>core.data_imports</c> etter en vellykket import.
/// Brukes av settlement-, SCADA- og operlog-handlerne for å mate
/// completeness-matrisen i SPEC-IMPORT-COMPLETENESS.
///
/// Plassert i Core slik at importør-moduler (Settlement, Scada) kan kalle
/// uten å peke til Reporting-modulen som bygger matrisen.
///
/// Feil-handling: importøren skal IKKE skrive denne raden hvis selve
/// importen feilet. Caller bestemmer rekkefølgen.
/// </summary>
public interface IDataImportLogger
{
    Task LogAsync(DataImportLogEntry entry, CancellationToken ct = default);
}

/// <summary>
/// Innkommende data fra en importør. <see cref="ImportId"/> genereres av
/// implementasjonen hvis null. <see cref="ImportedAtUtc"/> default = "nå".
/// </summary>
public sealed record DataImportLogEntry(
    string PlantId,
    string SourceType,                   // "settlement" (KAIA), "scada" (trender), "operlog" (alarmer)
    DateTimeOffset PeriodFromUtc,
    DateTimeOffset PeriodToUtc,
    string? FileName,
    string? FileHash,
    int? RowsImported,
    double? CoveragePct,
    string? UserId,
    string? Notes = null,
    DateTimeOffset? ImportedAtUtc = null,
    Guid? ImportId = null);
