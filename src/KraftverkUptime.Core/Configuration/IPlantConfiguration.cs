namespace KraftverkUptime.Core.Configuration;

/// <summary>
/// Anleggs-spesifikk konfigurasjon (marginalkostnad, spotpris-terskel,
/// egenforbruk, installert effekt, klassifiseringsparametre).
///
/// Justering (d) fra Prompt 1 v1: GetAsync er async – tidligere synkron Get
/// tvang cache-implementasjon og blokkerte tråder ved DB-oppslag.
///
/// V1-implementasjon: PostgreSQL-tabell "core.plant_configuration"
/// med IMemoryCache foran. Cache-invalidering ved SetAsync.
/// </summary>
public interface IPlantConfiguration
{
    /// <summary>Henter typed konfigurasjonsverdi. Returnerer default(T) hvis ikke satt.</summary>
    Task<T?> GetAsync<T>(string plantId, string key, CancellationToken ct = default);

    /// <summary>Setter konfigurasjonsverdi. Invaliderer cache.</summary>
    Task SetAsync<T>(string plantId, string key, T value, CancellationToken ct = default);
}
