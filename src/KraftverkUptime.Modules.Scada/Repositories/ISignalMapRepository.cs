using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Scada.Repositories;

/// <summary>
/// Lookup mot whitelisten <c>core.signal_map</c>. Caches gjerne in-memory
/// per anlegg (sjelden endring); foreløpig direkte DB-spørring.
/// </summary>
public interface ISignalMapRepository
{
    /// <summary>Alle aktive signaler for et anlegg, inklusive både StoreSamples=true/false.</summary>
    Task<IReadOnlyList<SignalMap>> ListForPlantAsync(string plantId, CancellationToken ct);

    /// <summary>Henter signal etter (plantId, signalId).</summary>
    Task<SignalMap?> GetAsync(string plantId, string signalId, CancellationToken ct);

    /// <summary>Returnerer signal-id for en gitt rolle ved et anlegg, eller null hvis ikke konfigurert.</summary>
    Task<string?> GetSignalIdForRoleAsync(string plantId, SignalRole role, CancellationToken ct);

    /// <summary>Upsert ved import — overskriver alle felter unntatt opprettet-tidspunkt.</summary>
    Task UpsertAsync(SignalMap signalMap, CancellationToken ct);
}
