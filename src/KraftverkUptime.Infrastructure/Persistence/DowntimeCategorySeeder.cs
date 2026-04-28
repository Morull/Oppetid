using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Idempotent seeder for de 7 default-nedetidskategoriene. Kalles fra
/// DatabaseBootstrapper etter at skjemaet er på plass. Eksisterende
/// system-kategorier oppdateres ikke automatisk — endringer på navn/farge
/// må gjøres via dedikert migrasjon.
/// </summary>
public static class DowntimeCategorySeeder
{
    private static readonly DowntimeCategoryEntry[] SystemCategories =
    [
        new()
        {
            Id = "scheduled_service",
            DisplayName = "Planlagt service / vedlikehold",
            ColorHex = "#1976D2",
            UnitStateOverride = UnitState.MaintenanceOutage,
            SortOrder = 10,
            IsActive = true,
            IsSystem = true
        },
        new()
        {
            Id = "scheduled_revision",
            DisplayName = "Planlagt revisjon",
            ColorHex = "#0D47A1",
            UnitStateOverride = UnitState.PlannedOutage,
            SortOrder = 20,
            IsActive = true,
            IsSystem = true
        },
        new()
        {
            Id = "fault",
            DisplayName = "Driftsfeil / havari",
            ColorHex = "#D32F2F",
            UnitStateOverride = UnitState.ForcedOutage,
            SortOrder = 30,
            IsActive = true,
            IsSystem = true
        },
        new()
        {
            Id = "grid_fault",
            DisplayName = "Nettsidefeil (Statnett)",
            ColorHex = "#C2185B",
            UnitStateOverride = UnitState.ForcedOutage,
            SortOrder = 40,
            IsActive = true,
            IsSystem = true
        },
        new()
        {
            Id = "weather",
            DisplayName = "Værhendelse",
            ColorHex = "#F57C00",
            UnitStateOverride = UnitState.ResourceUnavailable,
            SortOrder = 50,
            IsActive = true,
            IsSystem = true
        },
        new()
        {
            Id = "resource",
            DisplayName = "Vannmangel / hydrologi",
            ColorHex = "#00796B",
            UnitStateOverride = UnitState.ResourceUnavailable,
            SortOrder = 60,
            IsActive = true,
            IsSystem = true
        },
        new()
        {
            Id = "other",
            DisplayName = "Annet",
            ColorHex = "#616161",
            UnitStateOverride = UnitState.ForcedOutage,
            SortOrder = 90,
            IsActive = true,
            IsSystem = true
        }
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DowntimeCategorySeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        List<string> existing;
        try
        {
            existing = await db.DowntimeCategories
                .Select(c => c.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            // Tabellen finnes ikke ennå (typisk: kjører mot eksisterende dev-DB
            // før Initial/AddAnnotations-migrasjonene er generert og applikert).
            // Logg én gang og hopp over seedingen — neste oppstart etter
            // migrasjon vil seede normalt.
            logger.LogWarning(
                "Skipped downtime-category seed: 'core.downtime_categories' finnes ikke ennå. " +
                "Kjør 'Generer migrasjoner.bat' eller 'Tving full reset.bat'.");
            return;
        }
        var existingSet = existing.ToHashSet(StringComparer.Ordinal);

        var added = 0;
        foreach (var category in SystemCategories)
        {
            if (existingSet.Contains(category.Id))
            {
                continue;
            }
            db.DowntimeCategories.Add(category);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Seeded {Count} default downtime categories.", added);
        }
        else
        {
            logger.LogDebug("Default downtime categories already present, skipping seed.");
        }
    }

    /// <summary>
    /// Postgres SQLSTATE 42P01 = undefined_table. Kommer som
    /// <c>Npgsql.PostgresException</c>; vi sjekker meldingen via base-typen
    /// for å unngå hard avhengighet på Npgsql-pakken her.
    /// </summary>
    private static bool IsMissingRelation(Exception ex)
    {
        // Sjekk hele kjeden av inner exceptions for SQL-state 42P01.
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
            if (sqlState == "42P01")
            {
                return true;
            }
        }
        return false;
    }
}
