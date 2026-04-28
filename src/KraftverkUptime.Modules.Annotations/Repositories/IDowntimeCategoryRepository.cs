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
}
