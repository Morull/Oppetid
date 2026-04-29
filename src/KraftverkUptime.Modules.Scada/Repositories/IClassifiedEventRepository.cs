using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Scada.Repositories;

/// <summary>
/// Lese/skrive event-rader for state-endringer. Hver rad er én sammenhengende
/// drifts-tilstand med presis start (og evt. slutt). Brukes til:
/// – Sub-time trip-deteksjon fra operlog
/// – MTBF / MTTR / antall trip-events
/// – Audit-spor for hvordan klassifikator kom fram til en tilstand
/// </summary>
public interface IClassifiedEventRepository
{
    Task<IReadOnlyList<ClassifiedEvent>> ListAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    Task<long> CreateAsync(ClassifiedEvent ev, CancellationToken ct);

    /// <summary>Avslutt en åpen event ved å sette EndUtc.</summary>
    Task<bool> CloseAsync(long id, DateTimeOffset endUtc, CancellationToken ct);

    /// <summary>
    /// Idempotent upsert ved bulk-import: dedupliserer på (plant_id, start_utc, state)
    /// for å unngå dobbel-import av samme operlog-event.
    /// </summary>
    Task UpsertManyAsync(IReadOnlyCollection<ClassifiedEvent> events, CancellationToken ct);

    /// <summary>Sletter alle events for et anlegg. Returnerer antall slettede rader.</summary>
    Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct);

    /// <summary>Sletter alle events for organisasjonen. Returnerer antall slettede rader.</summary>
    Task<int> DeleteAllAsync(string ownerOrgId, CancellationToken ct);
}
