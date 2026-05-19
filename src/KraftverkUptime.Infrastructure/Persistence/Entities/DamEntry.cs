using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.dams</c>. Per-anlegg dam-katalog. Hvert anlegg har
/// én rad med <see cref="IsTurbineIntake"/> = true (terminal-dammen som
/// mater turbinen). Kaskade-anlegg har flere dammer; én-dam-anlegg har én.
///
/// Composite primary key (PlantId, DamId) speiler at DamId er per-anlegg.
/// Eksempler: ("drivdal", "drivdal_main"), ("haukland", "haukland_stemmevt").
/// </summary>
public sealed class DamEntry : IOwnedEntity
{
    public string PlantId { get; set; } = string.Empty;
    public string DamId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int CascadePosition { get; set; } = 1;
    public bool IsTurbineIntake { get; set; }
    public double? HrvMoh { get; set; }
    public double? LrvMoh { get; set; }
    public double? VolumeMm3 { get; set; }

    /// <summary>
    /// Terskel for level-baserte overflow-proxy (cm over HRV) — brukes når
    /// anleggets <see cref="PlantRegistration.OverflowMode"/> er
    /// <see cref="OverflowMode.LevelProxy"/> og dette er terminal-dammen.
    /// </summary>
    public int? OverflowProxyThresholdCm { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    // IOwnedEntity — multi-tenant filter via global query filter på (OwnerOrgId, PlantId)
    public string OwnerOrgId { get; set; } = string.Empty;
    string? IOwnedEntity.PlantId => PlantId;
}
