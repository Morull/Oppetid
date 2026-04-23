namespace KraftverkUptime.Modules.Settlement.Persistence;

/// <summary>
/// Persistens-seam for settlement-import-metadata. Implementeres i
/// Infrastructure (bak KraftverkDbContext); Settlement-modulen tar ikke
/// EF-avhengighet. Idempotens håndheves av implementasjonen via unik
/// constraint på (OwnerOrgId, PlantId, IdempotencyKey).
/// </summary>
public interface ISettlementImportRecorder
{
    /// <summary>
    /// Persister metadata om gjennomført import. Overskriver eksisterende rad
    /// med samme (OwnerOrgId, PlantId, IdempotencyKey) (upsert-semantikk).
    /// </summary>
    Task RecordAsync(SettlementImportRecord record, CancellationToken ct);

    /// <summary>
    /// Finn siste import for <paramref name="plantId"/> som dekker forespurt
    /// periode. En import "dekker" perioden hvis <c>PeriodStartUtc ≤ fromUtc</c>
    /// og <c>PeriodEndUtc ≥ toUtc</c>. Returnerer null hvis ingen import finnes.
    ///
    /// "Siste" = høyeste <c>ImportedAtUtc</c>.
    /// </summary>
    Task<SettlementImportRecord?> FindLatestCoveringAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);
}
