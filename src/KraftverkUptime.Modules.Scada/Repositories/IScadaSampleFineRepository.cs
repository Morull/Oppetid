using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Modules.Scada.Repositories;

/// <summary>
/// Bulk-INSERT og spørringer mot <c>core.sample_facts_fine</c> — separat tabell
/// for 15-min-oppløsnings SCADA-eksporter. API-kontrakt identisk med
/// <see cref="IScadaSampleRepository"/>, men peker på sin egen tabell slik at
/// 15-min-pipelinen ikke overskriver hourly-tabellen (eller omvendt) på
/// :00-tidsstempler. Spec NESTE-CHAT-EFFEKTIVITET-15MIN.md.
/// </summary>
public interface IScadaSampleFineRepository
{
    /// <summary>
    /// Skriver en batch av samples. Forventet idempotent på primary key
    /// (asset_id, signal_id, time_utc) — duplikater overskrives.
    /// </summary>
    Task<int> BulkInsertAsync(IReadOnlyCollection<ScadaSample> samples, CancellationToken ct);

    /// <summary>Henter alle samples for ett anlegg + flere signaler i et tidsvindu.</summary>
    Task<IReadOnlyList<ScadaSample>> ListAsync(
        string plantId,
        IReadOnlyCollection<string> signalIds,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct);

    /// <summary>Sletter alle samples for et anlegg eldre enn cutoff. Brukes til retensjon ved behov.</summary>
    Task<int> DeleteOlderThanAsync(string plantId, DateTimeOffset cutoffUtc, CancellationToken ct);

    /// <summary>Sletter ALLE samples for et anlegg. Brukes ved data-reset.</summary>
    Task<int> DeleteAllForPlantAsync(string plantId, CancellationToken ct);

    /// <summary>Sletter alle samples for organisasjonen. Brukes ved full system-reset.</summary>
    Task<int> DeleteAllAsync(CancellationToken ct);
}
