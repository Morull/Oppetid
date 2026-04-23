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

    /// <summary>
    /// Finn import via eksakt nøkkel. Returnerer null hvis ingen rad matcher
    /// <paramref name="plantId"/> + <paramref name="idempotencyKey"/>.
    /// </summary>
    Task<SettlementImportRecord?> FindByIdempotencyKeyAsync(
        string plantId,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>
    /// Lister importer for <paramref name="plantId"/> sortert nyeste først
    /// (etter <c>ImportedAtUtc</c>). Valgfritt filter på periode-overlapp:
    /// en import inkluderes hvis dens periode overlapper [<paramref name="fromUtc"/>,
    /// <paramref name="toUtc"/>]. Begge filter-parametere må være satt for å
    /// aktivere filtreringen; hvis begge er null returneres alle.
    /// </summary>
    /// <param name="plantId">Anleggs-ID importene filtreres på.</param>
    /// <param name="fromUtc">Nedre grense for periode-overlapp. Null = ingen filter.</param>
    /// <param name="toUtc">Øvre grense for periode-overlapp. Null = ingen filter.</param>
    /// <param name="limit">Maks antall rader som returneres. Positiv verdi.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SettlementImportRecord>> ListForPlantAsync(
        string plantId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int limit,
        CancellationToken ct);
}
