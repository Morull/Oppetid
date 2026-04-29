using KraftverkUptime.Modules.Classification.Dtos;

namespace KraftverkUptime.Modules.Reporting.Storage;

/// <summary>
/// Persistens-seam for ferdig-klassifiserte <see cref="UptimeReport"/>.
/// Infrastructure implementerer mot <c>IFileStorage</c> (JSON-serialisert blob);
/// modulen selv tar ikke lagringsavhengighet.
///
/// Nøkkelrom er <c>(OwnerOrgId, PlantId, IdempotencyKey)</c> – identisk med
/// <c>settlement_imports</c>-metadataen. Samme nøkkel overskriver eksisterende
/// rapport (upsert-semantikk).
/// </summary>
public interface IUptimeReportStore
{
    /// <summary>
    /// Persisterer en klassifiseringsrapport. Overskriver eksisterende
    /// objekt for samme nøkkel.
    /// </summary>
    Task SaveAsync(
        string ownerOrgId,
        string plantId,
        string idempotencyKey,
        UptimeReport report,
        CancellationToken ct);

    /// <summary>
    /// Henter en lagret rapport, eller <c>null</c> hvis ingen finnes for
    /// nøkkelen. Returnerer null både når rapporten aldri har blitt lagret
    /// og når den er eksplisitt slettet.
    /// </summary>
    Task<UptimeReport?> GetAsync(
        string ownerOrgId,
        string plantId,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>
    /// Sletter en spesifikk lagret rapport. No-op hvis den ikke finnes.
    /// </summary>
    Task DeleteAsync(
        string ownerOrgId,
        string plantId,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>
    /// Sletter alle rapporter for et anlegg. Returnerer antall slettede objekter.
    /// </summary>
    Task<int> DeleteAllForPlantAsync(
        string ownerOrgId,
        string plantId,
        CancellationToken ct);

    /// <summary>
    /// Sletter alle rapporter for organisasjonen. Returnerer antall slettede objekter.
    /// </summary>
    Task<int> DeleteAllAsync(
        string ownerOrgId,
        CancellationToken ct);
}
