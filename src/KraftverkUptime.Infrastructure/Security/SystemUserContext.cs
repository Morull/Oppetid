using KraftverkUptime.Core.Security;

namespace KraftverkUptime.Infrastructure.Security;

/// <summary>
/// V1 brukerkontekst. Alltid systemet – alle roller, tilgang til alle anlegg.
/// Brukes både i API (før Entra ID er koblet til) og i Worker (som aldri autentiserer en menneskelig bruker).
/// </summary>
public sealed class SystemUserContext : ICurrentUser
{
    public string UserId => "system";
    public string OrgId => "system";

    public IReadOnlySet<string> Roles { get; } =
        new HashSet<string>(KraftverkUptime.Core.Security.AuthorizationPolicies.All);

    public bool HasAllPlantsAccess => true;

    public IReadOnlySet<string> AccessiblePlantIds { get; } = new HashSet<string>();
}
