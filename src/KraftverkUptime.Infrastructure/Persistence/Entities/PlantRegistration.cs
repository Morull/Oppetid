using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// Anleggsregistrering. Minimal i v1 – utvides med enheter, settlement-mapping osv. i Prompt 2.
/// </summary>
public sealed class PlantRegistration : IOwnedEntity, ISoftDeletable
{
    public string Id { get; set; } = string.Empty;     // plantId
    public string OwnerOrgId { get; set; } = string.Empty;
    public string? PlantId => Id;
    public string Name { get; set; } = string.Empty;
    public PlantType Type { get; set; }
    public double InstalledCapacityMw { get; set; }
    public string TimeZone { get; set; } = "Europe/Oslo";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
