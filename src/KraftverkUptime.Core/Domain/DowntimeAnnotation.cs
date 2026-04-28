namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Manuell merknad på et tidsrom for ett anlegg. Overstyrer klassifikatorens
/// automatiske <see cref="UnitState"/> ved read-time merge i KPI-pipeline.
///
/// Tidsoppløsning: hele timer (UTC). Start er inklusiv, slutt er eksklusiv —
/// dvs. en annotering på 09:00–10:00 dekker kun timen 09:00.
///
/// Overlapp tvinges eksplisitt løst av kalleren (POST/PATCH med replaceIds).
/// Soft-delete via <see cref="DeletedAt"/>; aldri hard-delete fra API.
/// </summary>
public sealed record DowntimeAnnotation(
    long Id,
    string OwnerOrgId,
    string PlantId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string CategoryId,
    string? Comment,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    DateTimeOffset UpdatedAt,
    string? UpdatedBy,
    DateTimeOffset? DeletedAt,
    string? DeletedBy);
