namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Multi-tenant eierskap for en entitet. Global query filter i DbContext
/// begrenser tilgang ut fra <see cref="OwnerOrgId"/> og optional
/// <see cref="PlantId"/>. Alle domeneentiteter skal implementere dette.
/// </summary>
public interface IOwnedEntity
{
    /// <summary>Organisasjons-ID som eier raden. Aldri null.</summary>
    string OwnerOrgId { get; }

    /// <summary>Anleggs-ID som raden tilhører. Null for org-globale entiteter.</summary>
    string? PlantId { get; }
}
