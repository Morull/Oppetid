namespace KraftverkUptime.Core.Domain;

/// <summary>
/// En dam i et anleggs kaskade. Hvert anlegg har minst én dam med
/// <see cref="IsTurbineIntake"/> = true (den siste før turbinen). Kaskade-anlegg
/// har flere dammer der øvre dammer mater inn til lavere via lukeflyt.
///
/// Overløp på terminal-dam er den eneste som koster produksjon — overløp
/// på øvre dammer renner videre ned og kan fanges senere.
///
/// Backfill-strategi: hvert eksisterende anlegg får én default-dam med
/// DamId = "{plant}_main", CascadePosition = 1, IsTurbineIntake = true.
/// Eksisterende SignalMap-rader som er dam-relaterte oppdateres med samme DamId.
/// </summary>
public sealed record Dam(
    string PlantId,
    string DamId,
    string Name,
    int CascadePosition,
    bool IsTurbineIntake,
    double? HrvMoh,
    double? LrvMoh,
    double? VolumeMm3);
