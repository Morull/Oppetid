namespace KraftverkUptime.Modules.Reporting.DataCompleteness;

/// <summary>
/// Eksponerer status for forventet vs faktisk dataimport per anlegg per
/// periode. Bygger SPEC-IMPORT-COMPLETENESS-matrisen ved å krysse
/// <c>data_source_expectations</c> med <c>data_imports</c>.
///
/// Brukes av:
///   - <c>/data-status</c>-siden (matrise-rendering)
///   - Ukentlig digest-job (overdue + summary)
///   - API-eksterne integrasjoner via <c>/api/v1/data-status/...</c>
/// </summary>
public interface IDataCompletenessQueryService
{
    /// <summary>
    /// Bygger fullstendig matrise for periode-vinduet [fromUtc, toUtc).
    /// Hver celle inneholder status, sist-importert-tidspunkt og dekning.
    /// Anlegg uten aktive expectations blir ikke inkludert i celle-listen,
    /// men listes i <see cref="DataCompletenessMatrix.PlantIds"/> for
    /// kontekst.
    /// </summary>
    Task<DataCompletenessMatrix> GetMatrixAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>
    /// Returnerer kun OVERDUE-rader (manglende import + lag overskredet).
    /// Sortert etter <see cref="MissingImport.DaysOverdue"/> synkende.
    /// </summary>
    Task<IReadOnlyList<MissingImport>> GetOverdueAsync(CancellationToken ct);

    /// <summary>
    /// Aggregert sammendrag for ukentlig e-post-digest. Dekker gjeldende
    /// måned + forrige måned (vinduet hvor det er meningsfullt å mangle data).
    /// </summary>
    Task<DataCompletenessSummary> GetWeeklySummaryAsync(CancellationToken ct);

    /// <summary>
    /// Lister nylige importer (siste 24 timer) — brukes av auto-import-
    /// infobar på /data-import-siden så drifts-leder kan følge med på
    /// hva som er kommet inn og hva som behandles akkurat nå.
    /// </summary>
    Task<IReadOnlyList<RecentImport>> GetRecentImportsAsync(
        TimeSpan window, int limit, CancellationToken ct);
}

/// <summary>
/// Én nylig import — brukt i auto-import-infobar på /data-import.
/// </summary>
public sealed record RecentImport(
    Guid ImportId,
    string PlantId,
    string SourceType,
    DateTimeOffset PeriodFromUtc,
    DateTimeOffset PeriodToUtc,
    DateTimeOffset ImportedAtUtc,
    string? FileName,
    int? RowsImported,
    double? CoveragePct,
    string? UserId);

/// <summary>
/// Komplett matrise for et periode-vindu. <see cref="Cells"/>-dictionary
/// nøkkel = (PlantId, SourceType, Period). Manglende nøkler = ikke aktiv
/// kilde for det anlegget i den perioden.
/// </summary>
public sealed record DataCompletenessMatrix(
    IReadOnlyList<string> PlantIds,
    IReadOnlyList<string> SourceTypes,
    IReadOnlyList<DateTimeOffset> Periods,
    IReadOnlyDictionary<DataCompletenessKey, DataCompletenessCell> Cells);

/// <summary>
/// Composite-key. Brukt som dictionary-nøkkel i matrisen.
/// </summary>
public readonly record struct DataCompletenessKey(
    string PlantId,
    string SourceType,
    DateTimeOffset Period);

/// <summary>
/// Status for én (plant, source, period)-celle i matrisen.
/// <see cref="Status"/>: COMPLETE / PARTIAL / PENDING / OVERDUE.
///
/// For PARTIAL/COMPLETE-celler er import-detaljene (<see cref="ImportPeriodFromUtc"/>,
/// <see cref="RowsImported"/>, <see cref="FileName"/>) hentet fra den siste vinnende
/// importen for cellen. UI bruker disse for å vise "hva mangler" når brukeren
/// klikker på en delvis celle. Threshold er den effektive grensen for COMPLETE
/// (per-(plant,source) i <c>data_source_expectations.completion_threshold_pct</c>).
/// </summary>
public sealed record DataCompletenessCell(
    string Status,
    DateTimeOffset? LastImportedAt,
    double? CoveragePct,
    int ImportCount,
    DateTimeOffset? ImportPeriodFromUtc = null,
    DateTimeOffset? ImportPeriodToUtc = null,
    int? RowsImported = null,
    string? FileName = null,
    string? Notes = null,
    double? Threshold = null);

/// <summary>
/// En forventet import som ikke har kommet — sortert etter overdue-dager.
/// </summary>
public sealed record MissingImport(
    string PlantId,
    string SourceType,
    DateTimeOffset PeriodFromUtc,
    int DaysOverdue);

/// <summary>
/// Aggregert summary for ukentlig digest. Tellere på hver status + topp-N
/// overdue for prioritering.
/// </summary>
public sealed record DataCompletenessSummary(
    int TotalExpected,
    int Complete,
    int Partial,
    int Pending,
    int Overdue,
    IReadOnlyList<MissingImport> TopOverdue);
