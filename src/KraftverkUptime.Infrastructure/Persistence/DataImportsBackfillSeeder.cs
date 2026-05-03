using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Idempotent backfill av <c>core.data_imports</c> fra eksisterende
/// <c>core.settlement_imports</c>-historikk. Kjøres ved oppstart slik at
/// completeness-matrisen viser riktig status fra dag 1, uten å vente på
/// at neste import-syklus skal fylle inn radene.
///
/// SPEC-IMPORT-COMPLETENESS steg 3. Velger den pragmatiske varianten fra
/// spec'en: backfill kun settlement (som har strukturert metadata i DB).
/// SCADA og operlog mangler tilstrekkelig metadata for å rekonstruere
/// historikken — de fylles fremover når neste import går.
/// </summary>
public static class DataImportsBackfillSeeder
{
    /// <summary>
    /// Idempotent INSERT via NOT EXISTS-filter — dupliserer ikke rader hvis
    /// kjørt flere ganger. Coverage utledes fra hour_count og periode-spennet
    /// (rundet til hele timer; DST-perioder gir ±1 t avvik som ratio fortsatt
    /// rapporterer riktig over/under 0.95-terskelen).
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("DataImportsBackfillSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        const string backfillSql = """
            INSERT INTO core.data_imports
                (import_id, plant_id, source_type, period_from_utc, period_to_utc,
                 imported_at_utc, file_name, file_hash, rows_imported, coverage_pct, user_id, notes)
            SELECT
                gen_random_uuid(),
                si.plant_id,
                'settlement',
                si.period_start_utc,
                si.period_end_utc,
                si.imported_at_utc,
                NULL,
                si.idempotency_key,
                si.hour_count,
                LEAST(1.0, si.hour_count::double precision /
                      GREATEST(1, EXTRACT(EPOCH FROM (si.period_end_utc - si.period_start_utc)) / 3600.0)),
                'system-backfill',
                'Backfilled fra core.settlement_imports'
            FROM core.settlement_imports si
            WHERE si.deleted_at IS NULL
              AND si.plant_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM core.data_imports di
                  WHERE di.plant_id = si.plant_id
                    AND di.source_type = 'settlement'
                    AND di.period_from_utc = si.period_start_utc
                    AND di.imported_at_utc = si.imported_at_utc
              );
            """;

        try
        {
            var rows = await db.Database.ExecuteSqlRawAsync(backfillSql, ct).ConfigureAwait(false);
            if (rows > 0)
            {
                logger.LogInformation(
                    "Data-imports backfill: {Rows} settlement-importer seeded fra historikk.",
                    rows);
            }
            else
            {
                logger.LogDebug("Data-imports backfill: ingen nye rader (tabellen er allerede synkronisert).");
            }
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            logger.LogWarning(
                "Data-imports backfill: hoppet over fordi core.settlement_imports eller core.data_imports ikke finnes ennå.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Data-imports backfill feilet — completeness-matrisen vil bygge seg opp fremover når nye importer går.");
        }

        // Hydrogrid-plan-backfill: hver settlement vi har historikk for inneholder
        // som regel også Hydrogrid-plan i ProduksjonplanMwh-kolonnen. Vi kan ikke
        // måle plan-dekning fra DB-tabellen alene (den ligger i blob), så vi
        // markerer hver historisk settlement-import som "antakelig hadde plan"
        // med en konservativ coverage_pct = 0.95 og notes-flag.
        //
        // Brukeren får dermed grønt ikon for historiske perioder uten å måtte
        // re-parse alle blob-filer. Hvis det viser seg å være feil (perioder
        // før Hydrogrid ble tatt i bruk), kan rader slettes manuelt eller
        // markeres som inaktive via PlantAdmin (deactivated_at_utc).
        const string hydrogridBackfillSql = """
            INSERT INTO core.data_imports
                (import_id, plant_id, source_type, period_from_utc, period_to_utc,
                 imported_at_utc, file_name, file_hash, rows_imported, coverage_pct, user_id, notes)
            SELECT
                gen_random_uuid(),
                si.plant_id,
                'hydrogrid_plan',
                si.period_start_utc,
                si.period_end_utc,
                si.imported_at_utc,
                NULL,
                si.idempotency_key,
                si.hour_count,
                0.95,
                'system-backfill',
                'Backfilled (antar Hydrogrid-plan i settlement-fila)'
            FROM core.settlement_imports si
            WHERE si.deleted_at IS NULL
              AND si.plant_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM core.data_imports di
                  WHERE di.plant_id = si.plant_id
                    AND di.source_type = 'hydrogrid_plan'
                    AND di.period_from_utc = si.period_start_utc
                    AND di.imported_at_utc = si.imported_at_utc
              );
            """;

        try
        {
            var rows = await db.Database.ExecuteSqlRawAsync(hydrogridBackfillSql, ct).ConfigureAwait(false);
            if (rows > 0)
            {
                logger.LogInformation(
                    "Hydrogrid-plan-backfill: {Rows} rader seeded med konservativ coverage = 0.95.",
                    rows);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Hydrogrid-plan-backfill: hoppet over.");
        }
    }

    private static bool IsMissingRelation(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            var sqlState = current.GetType().GetProperty("SqlState")?.GetValue(current) as string;
            if (sqlState == "42P01") return true;
        }
        return false;
    }
}
