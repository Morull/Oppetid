using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.downtime_categories</c>. Global lookup-tabell —
/// implementerer ikke IOwnedEntity siden kategoriene deles på tvers av
/// organisasjoner. <see cref="IsSystem"/> markerer de 7 default-kategoriene
/// som seedes ved oppstart; disse kan ikke slettes (kun deaktiveres via
/// <see cref="IsActive"/>).
/// </summary>
public sealed class DowntimeCategoryEntry
{
    /// <summary>Stabil semantisk slug, f.eks. "scheduled_service".</summary>
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Hex-farge inkl. hash, f.eks. "#1976D2".</summary>
    public string ColorHex { get; set; } = "#888888";

    /// <summary>Lagres som strengnavn fra <see cref="UnitState"/> for migrasjons-vennlighet.</summary>
    public UnitState UnitStateOverride { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public bool IsSystem { get; set; }

    /// <summary>
    /// Fri-tekst-forklaring av kategorien. Vises i annoterings-dialogen og
    /// admin-skjermen. Drifts-leder kan redigere både for system- og
    /// bruker-kategorier — system-flagget styrer kun sletting, ikke
    /// metadata-oppdatering.
    /// </summary>
    public string? Description { get; set; }
}
