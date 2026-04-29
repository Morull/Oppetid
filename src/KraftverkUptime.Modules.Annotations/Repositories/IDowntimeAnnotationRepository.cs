using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Annotations.Repositories;

/// <summary>
/// CRUD og overlapp-deteksjon for nedetidsannoteringer. Lever bak query-context
/// så multi-tenant-filtreringen er automatisk; ingen kode utenfor repositoryet
/// skal lese annoterings-tabellen direkte.
/// </summary>
public interface IDowntimeAnnotationRepository
{
    /// <summary>Henter alle aktive (ikke-slettede) annoteringer for et anlegg som overlapper [fromUtc, toUtc).</summary>
    Task<IReadOnlyList<DowntimeAnnotation>> ListAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Henter én annotering på Id, eller null hvis ikke funnet/slettet.</summary>
    Task<DowntimeAnnotation?> GetAsync(long id, CancellationToken ct);

    /// <summary>
    /// Returnerer alle aktive annoteringer for samme anlegg som overlapper [startUtc, endUtc),
    /// eksklusive eventuell <paramref name="excludeId"/> (brukes ved PATCH).
    /// Brukes til å validere at klienten har angitt replaceIds for alle overlapp.
    /// </summary>
    Task<IReadOnlyList<DowntimeAnnotation>> FindOverlappingAsync(
        string plantId, DateTimeOffset startUtc, DateTimeOffset endUtc, long? excludeId, CancellationToken ct);

    /// <summary>Lagrer ny annotering. Returnerer Id på ny rad.</summary>
    Task<long> CreateAsync(DowntimeAnnotation annotation, CancellationToken ct);

    /// <summary>
    /// Oppdaterer eksisterende annotering (delvis: kun ikke-null felter).
    /// Returnerer den oppdaterte raden, eller null hvis ikke funnet.
    /// </summary>
    Task<DowntimeAnnotation?> UpdateAsync(
        long id,
        DateTimeOffset? startUtc,
        DateTimeOffset? endUtc,
        string? categoryId,
        string? comment,
        string? updatedBy,
        CancellationToken ct);

    /// <summary>Soft-delete. Returnerer true hvis raden eksisterte og ble markert.</summary>
    Task<bool> SoftDeleteAsync(long id, string? deletedBy, CancellationToken ct);

    /// <summary>
    /// Hard-sletter alle annoteringer for et anlegg (inkludert soft-deleted).
    /// Brukes ved data-reset. Returnerer antall slettede rader.
    /// </summary>
    Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct);
}
