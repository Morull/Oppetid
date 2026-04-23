using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

public sealed class PlantConfigurationEntry : IOwnedEntity, ISoftDeletable
{
    public long Id { get; set; }
    public string OwnerOrgId { get; set; } = string.Empty;
    public string? PlantId { get; set; }
    public string Key { get; set; } = string.Empty;

    /// <summary>Serialisert JSON.</summary>
    public string ValueJson { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
