using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

public sealed class AuditLogEntry : IOwnedEntity
{
    public long Id { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string UserId { get; set; } = string.Empty;
    public string OwnerOrgId { get; set; } = string.Empty;
    public string? PlantId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }

    /// <summary>Serialisert JSON. jsonb i Postgres.</summary>
    public string? PayloadJson { get; set; }
}
