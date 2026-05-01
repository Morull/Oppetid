using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Annotations.Repositories;

/// <summary>
/// Lookup-tabell for nedetidskategorier. Lese-tunge, sjelden mutasjon —
/// kan caches in-memory hvis vi ser DB-trykk i fremtiden.
/// </summary>
public interface IDowntimeCategoryRepository
{
    /// <summary>Returnerer alle aktive kategorier sortert på SortOrder, deretter DisplayName.</summary>
    Task<IReadOnlyList<DowntimeCategory>> ListActiveAsync(CancellationToken ct);

    /// <summary>Returnerer alle kategorier inkl. inaktive — for admin-skjermer.</summary>
    Task<IReadOnlyList<DowntimeCategory>> ListAllAsync(CancellationToken ct);

    /// <summary>Henter én kategori på Id (slug). Null hvis ikke finnes.</summary>
    Task<DowntimeCategory?> GetAsync(string id, CancellationToken ct);

    /// <summary>Oppretter en ny bruker-definert kategori (IsSystem=false).</summary>
    Task AddAsync(DowntimeCategory category, CancellationToken ct);

    /// <summary>
    /// Oppdaterer en eksisterende kategori. Selve <c>Id</c> er immutable.
    /// IsSystem-kategorier kan oppdateres mht. DisplayName/Color/SortOrder/
    /// IsActive, men ikke endre UnitStateOverride (mapping er fundamental).
    /// </summary>
    Task UpdateAsync(DowntimeCategory category, CancellationToken ct);

    /// <summary>
    /// Sletter en bruker-definert kategori. IsSystem-kategorier kan ikke
    /// slettes — bruk IsActive=false for å gjemme dem fra UI istedenfor.
    /// Kaster <see cref="InvalidOperationException"/> hvis kategorien er
    /// referert til av en eller flere annoteringer.
    /// </summary>
    Task DeleteAsync(string id, CancellationToken ct);
}
