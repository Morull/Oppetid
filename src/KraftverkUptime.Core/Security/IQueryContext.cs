using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Core.Security;

/// <summary>
/// Beriker en IQueryable med tenant/plant-filtre basert på aktiv brukerkontekst.
/// Brukes som global query filter i DbContext, og kan også brukes eksplisitt
/// av tjenester som selv bygger IQueryable.
/// V1: NoopQueryContext returnerer kilden uendret.
/// V2: filtrerer på OwnerOrgId = ICurrentUser.OrgId og (PlantId er null ELLER PlantId ∈ AccessiblePlantIds).
/// </summary>
public interface IQueryContext
{
    IQueryable<T> Apply<T>(IQueryable<T> source) where T : IOwnedEntity;
}
