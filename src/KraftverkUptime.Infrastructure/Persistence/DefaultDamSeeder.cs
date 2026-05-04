using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Sikrer at hvert anlegg har minst én dam markert som terminal-inntak
/// (<c>IsTurbineIntake = true</c>). Idempotent — hopper over plants som
/// allerede har minst én dam.
///
/// Bakgrunn: <c>EnsureDamsSchemaAsync</c> kjører før <c>PlantPortfolioSeeder</c>,
/// så backfill-SQL-en der finner ikke nylig-seedete plants. Denne seederen
/// kjøres ETTER plants er på plass og garanterer at hver plant kan brukes i
/// overflow-baserte beregninger (Vakt-ROI) selv om drifts-leder ennå ikke
/// har konfigurert HRV/LRV/volum eller dedikert kaskade i PlantAdmin.
///
/// Default-rad-en kan oppdateres senere via <c>PUT /api/v1/plants/{id}/dams/{damId}</c>
/// for å legge til HRV/LRV/Volum, eller suppleres med flere kaskade-dammer
/// via <c>POST /api/v1/plants/{id}/dams</c>.
/// </summary>
public static class DefaultDamSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("DefaultDamSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            // Bruker raw SQL slik at vi unngår global query filter på OwnerOrgId
            // (vi seeder for ALLE plants uavhengig av hvilken bruker som starter
            // appen). Idempotent: NOT EXISTS-filter hopper over plants som
            // allerede har dammer (eks. Haukland med 4-kaskaden).
            const string sql = """
                INSERT INTO core.dams
                    (plant_id, dam_id, name, cascade_position, is_turbine_intake, owner_org_id, created_at_utc)
                SELECT
                    p.id,
                    p.id || '_main',
                    p.name,
                    1,
                    TRUE,
                    p.owner_org_id,
                    NOW()
                FROM core.plants p
                WHERE NOT EXISTS (
                    SELECT 1 FROM core.dams d WHERE d.plant_id = p.id
                );
                """;

            var inserted = await db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
            if (inserted > 0)
            {
                logger.LogInformation(
                    "DefaultDamSeeder: opprettet {Count} default terminal-dammer (en per anlegg uten dam fra før).",
                    inserted);
            }
            else
            {
                logger.LogDebug("DefaultDamSeeder: alle anlegg har allerede minst én dam.");
            }

            // Tilbake-fyll signal_map: dam-relaterte roller på rader uten dam_id
            // får '<plant>_main' som default. Trygt fordi default-dammen alltid
            // er terminal — så overflow-tags peker riktig fra dag én.
            const string backfillSignalMapSql = """
                UPDATE core.signal_map sm
                SET dam_id = sm.plant_id || '_main'
                WHERE sm.dam_id IS NULL
                  AND sm.role IN (
                      'OverflowFlow',
                      'UpstreamLevel',
                      'DownstreamLevel',
                      'ReservoirFillFactor',
                      'LowestRegulatedLevel'
                  );
                """;
            var sigsUpdated = await db.Database.ExecuteSqlRawAsync(backfillSignalMapSql, ct).ConfigureAwait(false);
            if (sigsUpdated > 0)
            {
                logger.LogInformation(
                    "DefaultDamSeeder: oppdaterte {Count} signal_map-rader med default dam-id.",
                    sigsUpdated);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "DefaultDamSeeder feilet — eksisterende plants kan mangle terminal-dam, " +
                "Vakt-ROI vil rapportere data missing for disse.");
        }
    }
}
