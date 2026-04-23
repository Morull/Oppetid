namespace KraftverkUptime.Core.Security;

/// <summary>
/// Brukerkonteksten for den aktive forespørselen/jobben.
///
/// Justering (f) fra Prompt 1 v1: "tom AccessiblePlantIds = alle" var en skjult
/// og farlig konvensjon. Nå eksplisitt: <see cref="HasAllPlantsAccess"/> må være
/// true for bred tilgang; tom <see cref="AccessiblePlantIds"/> betyr "ingen".
/// </summary>
public interface ICurrentUser
{
    /// <summary>Unik bruker-ID (Entra ID-oid eller system-konstant "system").</summary>
    string UserId { get; }

    /// <summary>Organisasjons-ID som brukeren tilhører.</summary>
    string OrgId { get; }

    /// <summary>
    /// Rolle-sett fra Entra ID eller system-tilordning.
    /// Policy-lookup i ASP.NET bruker dette settet.
    /// </summary>
    IReadOnlySet<string> Roles { get; }

    /// <summary>
    /// True hvis brukeren kan se alle anlegg i organisasjonen (typisk OrgAdmin/SystemAdmin).
    /// Eksplisitt flagg, ikke en skjult konvensjon via tom liste.
    /// </summary>
    bool HasAllPlantsAccess { get; }

    /// <summary>
    /// Eksplisitt liste over anleggs-ID-er brukeren kan se.
    /// Tom = ingen plant-tilgang (med mindre <see cref="HasAllPlantsAccess"/> er true).
    /// </summary>
    IReadOnlySet<string> AccessiblePlantIds { get; }
}
