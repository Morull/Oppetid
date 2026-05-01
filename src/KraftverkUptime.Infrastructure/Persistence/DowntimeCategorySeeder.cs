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
            IsSystem = true,
            Description = "Forhåndsavtalt vedlikehold som er varslet inn — turbinservice, "
                + "smøring, kontrollsjekk, vannveis-tømming. Telles som MaintenanceOutage "
                + "(EAF reduseres, ikke FOR/AF). Bruk når jobben er planlagt > 24t i forveien."
        },
        new()
        {
            Id = "scheduled_revision",
            DisplayName = "Planlagt revisjon",
            ColorHex = "#0D47A1",
            UnitStateOverride = UnitState.PlannedOutage,
            SortOrder = 20,
            IsActive = true,
            IsSystem = true,
            Description = "Større planlagt revisjon — typisk årlig generator-revisjon, "
                + "dam-inspeksjon eller annen langvarig stans avtalt med Statnett. "
                + "Telles som PlannedOutage (EAF reduseres, ikke FOR/AF)."
        },
        new()
        {
            Id = "fault",
            DisplayName = "Driftsfeil / havari",
            ColorHex = "#D32F2F",
            UnitStateOverride = UnitState.ForcedOutage,
            SortOrder = 30,
            IsActive = true,
            IsSystem = true,
            Description = "Uvarslet havari på selve anlegget — generator-feil, lager-skade, "
                + "lukke-svikt, ledeapparat-stuck, hydraulikk-lekkasje, kjølesystem-svikt. "
                + "Telles som ForcedOutage (drar ned både FOR og AF). Default-valg når "
                + "klassifikatoren har plukket opp ForcedOutage og du skal annotere årsaken."
        },
        new()
        {
            Id = "grid_fault",
            DisplayName = "Nettsidefeil (Statnett)",
            ColorHex = "#C2185B",
            UnitStateOverride = UnitState.ForcedOutage,
            SortOrder = 40,
            IsActive = true,
            IsSystem = true,
            Description = "Anlegget gikk ned fordi nettet falt ut — typisk varslet "
                + "spenningssprang, kortslutning på linje, brytertripp eller Statnett-"
                + "anmodning om effektreduksjon. Skiller seg fra 'fault' ved at årsaken "
                + "ligger utenfor anleggets kontroll, men telles fortsatt som ForcedOutage."
        },
        new()
        {
            Id = "weather",
            DisplayName = "Værhendelse",
            ColorHex = "#F57C00",
            UnitStateOverride = UnitState.ResourceUnavailable,
            SortOrder = 50,
            IsActive = true,
            IsSystem = true,
            Description = "Ekstremvær som forhindrer drift — flom (overløp som ikke kan "
                + "passere turbin), frost/is i inntak, lyn-tripp, stormskade på kraftlinje. "
                + "Telles som ResourceUnavailable (gir ikke FOR-utslag). Skiller seg fra "
                + "'resource' ved at det er en kortvarig hendelse, ikke kronisk vannmangel."
        },
        new()
        {
            Id = "resource",
            DisplayName = "Vannmangel / hydrologi",
            ColorHex = "#00796B",
            UnitStateOverride = UnitState.ResourceUnavailable,
            SortOrder = 60,
            IsActive = true,
            IsSystem = true,
            Description = "Anlegget kunne ikke produsere fordi det var for lite vann i "
                + "magasinet — typisk tørke om sommeren, eller bevisst sparing inn mot "
                + "vinter. Telles som ResourceUnavailable (gir ikke FOR-utslag — det er "
                + "ingen feil, bare manglende ressurs)."
        },
        new()
        {
            Id = "other",
            DisplayName = "Annet",
            ColorHex = "#616161",
            UnitStateOverride = UnitState.ForcedOutage,
            SortOrder = 90,
            IsActive = true,
            IsSystem = true,
            Description = "Catch-all for hendelser som ikke passer i de andre kategoriene. "
                + "Skriv en tydelig kommentar i annoteringa slik at det er sporbart. "
                + "Vurder å lage en egen kategori hvis denne typen hendelser gjentar seg."
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

        // Idempotent: fyll inn description på eksisterende system-kategorier som
        // mangler den (DB-er som ble seedet før Description-feltet ble lagt til).
        // Endrer ikke description-er som brukeren har redigert manuelt.
        await BackfillDescriptionsAsync(db, logger, ct).ConfigureAwait(false);
    }

    private static async Task BackfillDescriptionsAsync(
        KraftverkDbContext db, ILogger logger, CancellationToken ct)
    {
        var defaults = SystemCategories.ToDictionary(c => c.Id, c => c.Description, StringComparer.Ordinal);
        var systemRows = await db.DowntimeCategories
            .Where(c => c.IsSystem && c.Description == null)
            .ToListAsync(ct).ConfigureAwait(false);
        if (systemRows.Count == 0) return;

        foreach (var row in systemRows)
        {
            if (defaults.TryGetValue(row.Id, out var desc))
            {
                row.Description = desc;
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Backfilled description for {Count} system-kategorier.", systemRows.Count);
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
