using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.classified_events</c>. Én rad per state-endring
/// med presis tidsstempel. Implementerer IOwnedEntity for multi-tenant
/// query-filter.
/// </summary>
public sealed class ClassifiedEventEntry : IOwnedEntity
{
    public long Id { get; set; }
    public string OwnerOrgId { get; set; } = string.Empty;
    public string PlantId { get; set; } = string.Empty;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
    public UnitState State { get; set; }
    public string? CauseCode { get; set; }
    public double Confidence { get; set; }

    /// <summary>JSON-array med kilder, f.eks. ["Scada","Operlog"].</summary>
    public string SourcesJson { get; set; } = "[]";

    public string? Rationale { get; set; }

    string? IOwnedEntity.PlantId => PlantId;
}
