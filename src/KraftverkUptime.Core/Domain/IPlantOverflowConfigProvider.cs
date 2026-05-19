namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Narrow read-only oppslag for hvilken <see cref="OverflowMode"/> et anlegg
/// bruker. Brukes av Vakt-ROI-pipeline for å velge riktig overflow-strategi
/// (native tag / level-proxy / produksjons-historikk) per anlegg uten å dra
/// inn hele PlantRegistration-entiteten.
/// </summary>
public interface IPlantOverflowConfigProvider
{
    /// <summary>
    /// Returnerer anleggets <see cref="OverflowMode"/>. Returnerer
    /// <see cref="OverflowMode.NativeTag"/> som default for ukjent plantId
    /// — det er den minst antagende verdien (bruk SCADA-tag hvis den finnes).
    /// </summary>
    Task<OverflowMode> GetOverflowModeAsync(string plantId, CancellationToken ct);
}
