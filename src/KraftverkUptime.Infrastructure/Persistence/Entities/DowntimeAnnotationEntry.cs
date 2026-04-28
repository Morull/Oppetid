using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.downtime_annotations</c>. Manuell merknad på et
/// tidsrom for ett anlegg. Soft-delete via <see cref="DeletedAt"/>; rader
/// med satt DeletedAt skjules av global query filter.
/// </summary>
public sealed class DowntimeAnnotationEntry : IOwnedEntity, ISoftDeletable
{
    public long Id { get; set; }

    // IOwnedEntity
    public string OwnerOrgId { get; set; } = string.Empty;
    public string? PlantId { get; set; }

    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset EndUtc { get; set; }

    public string CategoryId { get; set; } = string.Empty;
    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }

    // ISoftDeletable
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
