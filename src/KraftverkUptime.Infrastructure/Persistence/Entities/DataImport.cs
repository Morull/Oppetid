namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.data_imports</c>. Logger hver vellykket import
/// (settlement, SCADA, operlog, Hydrogrid-plan) med metadata til bruk i
/// completeness-matrisen og feilsøkings-spor.
///
/// SPEC-IMPORT-COMPLETENESS: hver eksisterende importør skriver én rad
/// her etter vellykket prosessering. Brukes av <c>data_completeness_view</c>
/// til å avgjøre om en periode er COMPLETE / PARTIAL / PENDING / OVERDUE.
///
/// Idempotens: vi tillater multiple rader for samme (plant, source, period)
/// — typisk re-import med korrigert fil. <c>last_imported_at</c>-aggregering
/// i view-en gjør at siste import vinner.
/// </summary>
public sealed class DataImport
{
    public Guid ImportId { get; set; } = Guid.NewGuid();

    public string PlantId { get; set; } = string.Empty;

    /// <summary>"settlement", "scada", "operlog", "hydrogrid_plan".</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>Periode-start UTC (typisk månedsstart for monthly-cadence).</summary>
    public DateTimeOffset PeriodFromUtc { get; set; }

    /// <summary>Periode-slutt UTC (eksklusiv: månedsstart + 1 måned).</summary>
    public DateTimeOffset PeriodToUtc { get; set; }

    public DateTimeOffset ImportedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string? FileName { get; set; }

    /// <summary>SHA-256 hex av kildefila — for å fange opp re-import av samme fil.</summary>
    public string? FileHash { get; set; }

    public int? RowsImported { get; set; }

    /// <summary>
    /// Andel av forventet dekning [0..1]. Eks: settlement med 670/672 timer = 0.997.
    /// PARTIAL-status hvis &lt; 0.95.
    /// </summary>
    public double? CoveragePct { get; set; }

    /// <summary>Bruker som trigget importen ("system" for backfill / async jobber).</summary>
    public string? UserId { get; set; }

    public string? Notes { get; set; }
}
