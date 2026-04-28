using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.signal_map</c>. Whitelist per anlegg: hvilke SCADA-tags
/// vi importerer og hvilken rolle de spiller. CSV-importeren matcher
/// CsvColumn mot eksport-filens header.
/// </summary>
public sealed class SignalMapEntry : IOwnedEntity
{
    public string PlantId { get; set; } = string.Empty;
    public string SignalId { get; set; } = string.Empty;
    public string CsvColumn { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public SignalRole Role { get; set; }
    public bool StoreSamples { get; set; } = true;
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // IOwnedEntity — multi-tenant filter via global query filter
    public string OwnerOrgId { get; set; } = string.Empty;
    string? IOwnedEntity.PlantId => PlantId;
}
