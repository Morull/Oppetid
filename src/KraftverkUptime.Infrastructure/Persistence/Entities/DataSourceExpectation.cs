namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.data_source_expectations</c>. Per anlegg per
/// kilde-type definerer hvor ofte vi forventer data, og hvor mange dager
/// etter periode-slutt vi flagger en manglende import som forfalt.
///
/// SPEC-IMPORT-COMPLETENESS: brukes av <c>data_completeness_view</c>
/// til å krysse forventninger mot faktiske importer (<c>data_imports</c>).
///
/// PK = (PlantId, SourceType): én rad per kombinasjon. Kilden kan slås av
/// midlertidig via <see cref="IsActive"/> uten å miste konfigurasjons-historikk.
/// </summary>
public sealed class DataSourceExpectation
{
    /// <summary>Plant-id som matcher <see cref="PlantRegistration.Id"/>.</summary>
    public string PlantId { get; set; } = string.Empty;

    /// <summary>"settlement", "scada", "operlog", "hydrogrid_plan".</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>"monthly", "weekly", "daily", "continuous".</summary>
    public string Cadence { get; set; } = "monthly";

    /// <summary>
    /// Antall dager etter periode-slutt vi forventer import. Etter dette
    /// flagges manglende import som OVERDUE i datakvalitets-matrisen.
    /// Settlement: 7 (KAIA-leveranse rundt 5. virkedag i ny måned).
    /// SCADA: 5 (eksport tidligere, manuell prosess hos drifts-leder).
    /// </summary>
    public int ExpectedLagDays { get; set; }

    /// <summary>
    /// Toggle for å skru av forventning uten å slette den. Inaktiv kilde
    /// skjules fra dashboard-matrisen i stedet for å vises som OVERDUE.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Når kilden ble aktivert første gang. Brukes til å begrense
    /// hvilke perioder som forventes (vi ber ikke om data fra før kilden
    /// fantes).
    /// </summary>
    public DateTimeOffset? ActivatedAtUtc { get; set; }

    /// <summary>Når kilden sist ble deaktivert (kan være null).</summary>
    public DateTimeOffset? DeactivatedAtUtc { get; set; }

    /// <summary>
    /// Dekningsgrense for å regnes som COMPLETE (default 0.95).
    /// Coverage_pct &lt; denne → PARTIAL. Per-(plant, source)-konfigurerbar
    /// fordi forskjellige kilder har forskjellige forventninger:
    ///   - Settlement: 0.95 (KAIA-fila har som regel 670/672 timer)
    ///   - SCADA: 0.80 (snapshots og periodiske eksporter har naturlig hull)
    ///   - Operlog: 0.95 (events spores nøyaktig)
    /// Drifts-leder kan justere per anlegg via PlantAdmin.
    /// </summary>
    public double CompletionThresholdPct { get; set; } = 0.95;
}
