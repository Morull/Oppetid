namespace KraftverkUptime.Core.Security;

/// <summary>
/// Navngitte autorisasjonspolicyer som brukes av Minimal API-endepunktene.
/// Policy-implementasjon lever i Infrastructure/Api; modulkode refererer bare navnene.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>Lesetilgang til anleggs-data og rapporter.</summary>
    public const string PlantReader = nameof(PlantReader);

    /// <summary>Kjøre analyser og generere ad-hoc-rapporter for tilgjengelige anlegg.</summary>
    public const string PlantAnalyst = nameof(PlantAnalyst);

    /// <summary>Konfigurere anlegg (marginalkostnad, terskelverdier, egenforbruk).</summary>
    public const string PlantAdmin = nameof(PlantAdmin);

    /// <summary>Administrere brukere og anlegg innen egen organisasjon.</summary>
    public const string OrgAdmin = nameof(OrgAdmin);

    /// <summary>Plattform-administrasjon, kryss-organisasjons-sysadmin.</summary>
    public const string SystemAdmin = nameof(SystemAdmin);

    public static IReadOnlyList<string> All { get; } =
        [PlantReader, PlantAnalyst, PlantAdmin, OrgAdmin, SystemAdmin];
}
