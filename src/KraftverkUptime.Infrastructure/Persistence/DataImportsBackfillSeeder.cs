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

        // Rydd bort tidligere seedet hydrogrid_plan-rader. Drifts-leders
        // 2026-05-03-bekreftelse: kun 3 source types (settlement, scada,
        // operlog). Hydrogrid-plan er en kolonne i settlement-fila og
        // spores ikke separat. Idempotent — DELETE WHERE source_type = 'hydrogrid_plan'.
        try
        {
            var deleted = await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM core.data_imports WHERE source_type = 'hydrogrid_plan';", ct)
                .ConfigureAwait(false);
            if (deleted > 0)
            {
                logger.LogInformation(
                    "Ryddet bort {Deleted} hydrogrid_plan-rader fra data_imports (konsolidert i settlement).",
                    deleted);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "hydrogrid_plan-cleanup: hoppet over.");
        }

        // Backfill activated_at_utc for tidligere auto-aktiverte rader hvor
        // datoen ble satt til "nå" (i tidligere versjoner av DbDataImportLogger).
        // Hvis activated_at er nyere enn et historisk import-tidspunkt for
        // samme plant+source, vil status-matrisen filtrere bort den perioden.
        // Fix: dra activated_at tilbake til 2024-01-01 for hydrogrid_plan + scada
        // + operlog-rader hvor activated_at > eldste imported_at_utc.
        const string fixActivationDateSql = """
            UPDATE core.data_source_expectations e
            SET activated_at_utc = '2024-01-01T00:00:00+00:00'::timestamptz
            WHERE EXISTS (
                SELECT 1 FROM core.data_imports i
                WHERE i.plant_id = e.plant_id
                  AND i.source_type = e.source_type
                  AND i.period_from_utc < e.activated_at_utc
            );
            """;
        try
        {
            var fixed_ = await db.Database.ExecuteSqlRawAsync(fixActivationDateSql, ct).ConfigureAwait(false);
            if (fixed_ > 0)
            {
                logger.LogInformation(
                    "Justerte activated_at_utc tilbake til 2024-01-01 for {Count} expectations " +
                    "som hadde historikk fra før activation-tidspunktet.", fixed_);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "activation-date-fix: hoppet over.");
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
