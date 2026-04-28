namespace KraftverkUptime.Core.Domain;

/// <summary>
/// Lookup for nedetidskategorier. <see cref="Id"/> er en stabil semantisk slug
/// (f.eks. "scheduled_service") som brukes som referanse fra
/// <see cref="DowntimeAnnotation.CategoryId"/>. Eksposert til frontend slik at
/// dialogen kan rendre liste, farge og default-valg.
///
/// <see cref="UnitStateOverride"/> bestemmer hvordan annoterte timer mappes
/// til <see cref="UnitState"/> ved overlay. Datadrevet — nye kategorier kan
/// legges til uten å endre overlay-koden.
///
/// <see cref="IsSystem"/> markerer de 7 default-kategoriene som seedes ved
/// oppstart. System-kategorier kan ikke slettes; brukere kan deaktivere via
/// <see cref="IsActive"/>.
/// </summary>
public sealed record DowntimeCategory(
    string Id,
    string DisplayName,
    string ColorHex,
    UnitState UnitStateOverride,
    int SortOrder,
    bool IsActive,
    bool IsSystem);
