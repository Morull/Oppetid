using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;

namespace KraftverkUptime.Infrastructure.Security;

/// <summary>
/// V1-implementasjon. Returnerer kildekvelet uten filtrering. SystemUserContext har full tilgang.
/// V2 (EntraIdUserContext) erstatter denne med en versjon som filtrerer på OwnerOrgId og AccessiblePlantIds.
/// </summary>
public sealed class NoopQueryContext : IQueryContext
{
    public IQueryable<T> Apply<T>(IQueryable<T> source) where T : IOwnedEntity => source;
}
