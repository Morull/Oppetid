using KraftverkUptime.Core.Security;
using Microsoft.AspNetCore.Authorization;

namespace KraftverkUptime.Api.Authorization;

public static class AuthorizationExtensions
{
    /// <summary>
    /// Registrerer alle policies fra <see cref="AuthorizationPolicies"/>.
    /// V1: hver policy returnerer "allow" fordi <see cref="Infrastructure.Security.SystemUserContext"/> er aktiv.
    /// V2: bytt til rolle-/claims-baserte sjekker uten å endre endepunkter.
    ///
    /// FallbackPolicy=PlantReader: nye endepunkter uten eksplisitt
    /// <c>RequireAuthorization(...)</c> arver automatisk lese-krav. Forhindrer
    /// at man ved et uhell publiserer et anonymt endepunkt — defense-in-depth.
    /// Health-endepunkter må eksplisitt taggene <c>AllowAnonymous()</c> for
    /// å overstyre fallback.
    /// </summary>
    public static IServiceCollection AddKraftverkAuthorization(this IServiceCollection services)
    {
        var fallback = new AuthorizationPolicyBuilder()
            .RequireAssertion(_ => true) // V1: matcher PlantReader-placeholder. V2: krev innlogget bruker.
            .Build();

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.PlantReader,   p => p.RequireAssertion(_ => true))
            .AddPolicy(AuthorizationPolicies.PlantAnalyst,  p => p.RequireAssertion(_ => true))
            .AddPolicy(AuthorizationPolicies.PlantAdmin,    p => p.RequireAssertion(_ => true))
            .AddPolicy(AuthorizationPolicies.OrgAdmin,      p => p.RequireAssertion(_ => true))
            .AddPolicy(AuthorizationPolicies.SystemAdmin,   p => p.RequireAssertion(_ => true))
            .SetFallbackPolicy(fallback);

        return services;
    }
}
