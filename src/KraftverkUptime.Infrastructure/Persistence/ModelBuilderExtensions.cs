using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Security;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Reflection;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Hjelpefunksjoner for globale filtre. Håndhever fallgrube 4: global query filter må aktiveres per entitet.
/// </summary>
internal static class ModelBuilderExtensions
{
    /// <summary>
    /// Påfører soft-delete og tenant-/plant-filter på alle entiteter som implementerer relevante interfaces.
    /// </summary>
    public static void ApplyOwnedEntityFilters(this ModelBuilder modelBuilder, IQueryContext queryContext)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            var isOwned = typeof(IOwnedEntity).IsAssignableFrom(clrType);
            var isSoftDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);

            if (!isOwned && !isSoftDeletable)
            {
                continue;
            }

            // Bygg filter-lambda: x => (!softDeleted) && (isOwned_ok_by_context)
            var parameter = Expression.Parameter(clrType, "x");
            Expression? body = null;

            if (isSoftDeletable)
            {
                var deletedAt = Expression.Property(parameter, nameof(ISoftDeletable.DeletedAt));
                var notDeleted = Expression.Equal(deletedAt, Expression.Constant(null, typeof(DateTimeOffset?)));
                body = notDeleted;
            }

            // Tenant-filter i v1: no-op (SystemUserContext). Struktur på plass slik at v2 bare bytter implementasjon.
            // I v2 utvides denne til å hente AccessiblePlantIds fra ICurrentUser/IQueryContext.

            if (body is not null)
            {
                var lambda = Expression.Lambda(body, parameter);
                modelBuilder.Entity(clrType).HasQueryFilter(lambda);
            }

            // Indekser på OwnerOrgId og PlantId hvis de finnes som properties på entiteten.
            if (isOwned)
            {
                TryIndexProperty(modelBuilder, clrType, nameof(IOwnedEntity.OwnerOrgId));

                if (HasMaterialProperty(clrType, nameof(IOwnedEntity.PlantId)))
                {
                    TryIndexProperty(modelBuilder, clrType, nameof(IOwnedEntity.PlantId));
                }
            }

            _ = queryContext; // parameter holdes for framtidig berikelse i v2
        }
    }

    private static void TryIndexProperty(ModelBuilder modelBuilder, Type clrType, string propertyName)
    {
        var property = clrType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            return;
        }

        // Unngå å indeksere computed / unmapped properties.
        if (property.GetMethod?.IsVirtual == true && property.SetMethod is null)
        {
            return;
        }

        modelBuilder.Entity(clrType).HasIndex(propertyName);
    }

    private static bool HasMaterialProperty(Type clrType, string name)
    {
        var prop = clrType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return prop is not null && prop.SetMethod is not null;
    }
}
