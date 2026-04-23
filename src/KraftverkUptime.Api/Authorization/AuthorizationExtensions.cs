using KraftverkUptime.Core.Security;
using Microsoft.AspNetCore.Authorization;

namespace KraftverkUptime.Api.Authorization;

public static class AuthorizationExtensions
{
    /// <summary>
    /// Registrerer alle policies fra <see cref="AuthorizationPolicies"/>.
    /// V1: hver policy returnerer "allow" fordi <see cref="Infrastructure.Security.SystemUserContext"/> er aktiv.
    /// V2: bytt til rolle-/claims-baserte sjekker uten å endre endepunkter.
    /// </summary>
    public static IServiceCollection AddKraftverkAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.PlantReader,   p => p.RequireAssertion(_ => true))
            .AddPolicy(AuthorizationPolicies.PlantAnalyst,  p => p.RequireAssertion(_ => true))
            .AddPolicy(AuthorizationPolicies.PlantAdmin,    p => p.RequireAssertion(_ => true))
            .AddPolicy(AuthorizationPolicies.OrgAdmin,      p => p.RequireAssertion(_ => true))
            .AddPolicy(AuthorizationPolicies.SystemAdmin,   p => p.RequireAssertion(_ => true));

        return services;
    }
}
