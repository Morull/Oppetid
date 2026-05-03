using KraftverkUptime.Infrastructure.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Kjører migreringer ved oppstart når konfigurasjonen tillater det. Brukes typisk i dev og staging.
/// I prod skal migreringer kjøres som eget steg i CI/CD – sett RunMigrationsOnStartup = false.
/// </summary>
public static class DatabaseBootstrapper
{
    public static async Task ApplyMigrationsAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseBootstrapper");
        var options = scope.ServiceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        if (!options.RunMigrationsOnStartup)
        {
            logger.LogInformation("Database migrations are disabled at startup.");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            var pending = await db.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false);
            var list = pending.ToList();
            if (list.Count > 0)
            {
                logger.LogInformation("Applying {Count} pending migrations.", list.Count);
                await db.Database.MigrateAsync(ct).ConfigureAwait(false);
            }
            else
            {
                logger.LogInformation("No pending migrations. Ensuring database is created (first run).");
                // Ingen migreringer generert ennå (f.eks. helt nytt checkout) – fall tilbake til EnsureCreated
                // slik at dev-oppstarten ikke kræsjer. Fjern dette fallbacket når første migrering er lagt til.
                var created = await db.Database.EnsureCreatedAsync(ct).ConfigureAwait(false);
                if (created)
                {
                    logger.LogWarning("Database schema ble opprettet med EnsureCreated. Kjør 'dotnet ef migrations add Initial' og commit migreringen før prod.");
                }
            }

            // Bro mellom EnsureCreated-flyten og fremtidige EF-migrasjoner: sikrer at
            // annoteringstabellene alltid finnes på eksisterende DB-er. Idempotent — kan
            // fjernes så snart Initial/AddAnnotations EF-migrasjoner er generert og applikert.
            await EnsureAnnotationsSchemaAsync(db, logger, ct).ConfigureAwait(false);

            // SCADA foundation — ref ANALYSE-NEDETID-SCADA.md. Idempotent.
            // Forsøker også å aktivere TimescaleDB-extension; failer stille
            // hvis postgres-imaget ikke har den (vanilla Postgres = OK).
            await EnsureScadaSchemaAsync(db, logger, ct).ConfigureAwait(false);

            // Market prices + plant.price_area for capture rate (Spec CAPTURE-RATE).
            // Idempotent; kan fjernes når EF-migrasjoner tar over skjema-styringen.
            await EnsureMarketPricesSchemaAsync(db, logger, ct).ConfigureAwait(false);

            // Dams + signal_map.dam_id for kaskade-modellen (Spec KASKADE-DAMMER).
            // Idempotent; backfill sikrer at alle eksisterende anlegg får én default-dam.
            await EnsureDamsSchemaAsync(db, logger, ct).ConfigureAwait(false);

            // Data-completeness-tabeller (SPEC-IMPORT-COMPLETENESS).
            // Idempotent; backfill seeder default-expectations for alle plants.
            await EnsureDataCompletenessSchemaAsync(db, logger, ct).ConfigureAwait(false);

            // Seed default-nedetidskategorier (idempotent — hopper over hvis allerede tilstede).
            await DowntimeCategorySeeder.SeedAsync(services, ct).ConfigureAwait(false);

            // Bootstrap hele Dalane Kraft-porteføljen (11 anlegg). Idempotent.
            // Kjøres FØR DrivdalSignalMapSeeder slik at andre anlegg eksisterer
            // når deres SCADA blir konfigurert senere.
            await PlantPortfolioSeeder.SeedAsync(services, ct).ConfigureAwait(false);

            // Seed Drivdal-signal-map (22 tags). Idempotent. Andre anlegg legges
            // inn manuelt eller via egen seeder etter samme mønster.
            await DrivdalSignalMapSeeder.SeedAsync(services, ct).ConfigureAwait(false);

            // Seed Haukland-signal-map (195 tags) + 4-dam-kaskade. Idempotent.
            // Erstatter den default 'haukland_main'-dammen som backfill opprettet
            // med Stølsvatn/Gjelevatn/Skårstemmevatn/Stemmevatn (terminal).
            await HauklandSignalMapSeeder.SeedAsync(services, ct).ConfigureAwait(false);

            // Backfill data_imports fra eksisterende settlement_imports-historikk
            // (SPEC-IMPORT-COMPLETENESS steg 3). Idempotent — NOT EXISTS-filter.
            await DataImportsBackfillSeeder.SeedAsync(services, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database bootstrap failed.");
            throw;
        }
    }

    /// <summary>
    /// Idempotent skjema-bro for <c>core.downtime_categories</c> og
    /// <c>core.downtime_annotations</c>. Erstatter <c>Patch annotation tables.bat</c> —
    /// kjører automatisk ved oppstart slik at eksisterende dev-DB-er får de nye
    /// tabellene uten manuelle steg.
    ///
    /// Identisk DDL som i Patch-bat-filen. Når EF-migrasjoner genereres
    /// (<c>Generer migrasjoner.bat</c>) tar de over ansvaret, og denne metoden
    /// kan slettes.
    /// </summary>
    private static async Task EnsureAnnotationsSchemaAsync(
        KraftverkDbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS core.downtime_categories (
                id varchar(64) PRIMARY KEY,
                display_name varchar(200) NOT NULL,
                color_hex varchar(16) NOT NULL DEFAULT '#888888',
                unit_state_override varchar(32) NOT NULL,
                sort_order integer NOT NULL DEFAULT 0,
                is_active boolean NOT NULL DEFAULT TRUE,
                is_system boolean NOT NULL DEFAULT FALSE
            );

            CREATE INDEX IF NOT EXISTS ix_downtime_categories_sort_order
                ON core.downtime_categories (sort_order);

            CREATE TABLE IF NOT EXISTS core.downtime_annotations (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                owner_org_id varchar(64) NOT NULL,
                plant_id varchar(64) NOT NULL,
                start_utc timestamptz NOT NULL,
                end_utc timestamptz NOT NULL,
                category_id varchar(64) NOT NULL,
                comment varchar(2000) NULL,
                created_at timestamptz NOT NULL,
                created_by varchar(128) NULL,
                updated_at timestamptz NOT NULL,
                updated_by varchar(128) NULL,
                deleted_at timestamptz NULL,
                deleted_by varchar(128) NULL,
                CONSTRAINT fk_downtime_annotations_category
                    FOREIGN KEY (category_id)
                    REFERENCES core.downtime_categories(id)
                    ON DELETE RESTRICT
            );

            CREATE INDEX IF NOT EXISTS ix_downtime_annotations_plant_period
                ON core.downtime_annotations (plant_id, start_utc, end_utc)
                WHERE deleted_at IS NULL;

            -- Description-kolonne for kategorier (Spec: brukerredigerbar
            -- trigger-forklaring). Idempotent ALTER med IF NOT EXISTS.
            ALTER TABLE core.downtime_categories
                ADD COLUMN IF NOT EXISTS description varchar(2000) NULL;
            """;

        try
        {
            await db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
            logger.LogDebug("Annotation schema bridge ensured (downtime_categories, downtime_annotations).");
        }
        catch (Exception ex)
        {
            // Ikke fatal — logg og la oppstarten fortsette. Seederen vil deretter
            // detektere manglende tabell og skippe stille (se DowntimeCategorySeeder).
            logger.LogWarning(ex, "Kunne ikke sikre annotation-skjemaet. Fortsetter uten det.");
        }
    }

    /// <summary>
    /// Idempotent skjema-bro for SCADA-tabellene (signal_map, sample_facts,
    /// classified_events). Forsøker også å aktivere TimescaleDB-extension
    /// for hypertable-optimalisering; failer stille om postgres-imaget ikke
    /// har den. Tabellene fungerer som vanlige Postgres-tabeller uten
    /// TimescaleDB — bare uten partisjonerings-fordelene.
    /// </summary>
    private static async Task EnsureScadaSchemaAsync(
        KraftverkDbContext db,
        ILogger logger,
        CancellationToken ct)
    {
        // 1) Forsøk å aktivere TimescaleDB. Tre mulige utfall:
        //    a) Imaget har extension OG den er aktivert → vi går videre med hypertables
        //    b) Imaget har extension men ikke aktivert → CREATE EXTENSION lykkes
        //    c) Imaget mangler extension (vanilla postgres) → catch, fortsett uten
        var hasTimescale = false;
        try
        {
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS timescaledb", ct)
                .ConfigureAwait(false);
            hasTimescale = true;
            logger.LogInformation("TimescaleDB extension aktivert.");
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                "TimescaleDB ikke tilgjengelig i postgres-imaget — fortsetter med vanlige tabeller. ({Message})",
                ex.Message);
        }

        const string baseTablesSql = """
            CREATE TABLE IF NOT EXISTS core.signal_map (
                plant_id varchar(64) NOT NULL,
                signal_id varchar(128) NOT NULL,
                csv_column varchar(256) NOT NULL,
                unit varchar(32) NOT NULL,
                role varchar(64) NOT NULL,
                store_samples boolean NOT NULL DEFAULT TRUE,
                is_active boolean NOT NULL DEFAULT TRUE,
                created_at timestamptz NOT NULL DEFAULT NOW(),
                updated_at timestamptz NOT NULL DEFAULT NOW(),
                owner_org_id varchar(64) NOT NULL,
                PRIMARY KEY (plant_id, signal_id)
            );

            CREATE INDEX IF NOT EXISTS ix_signal_map_plant ON core.signal_map (plant_id);
            CREATE INDEX IF NOT EXISTS ix_signal_map_plant_role ON core.signal_map (plant_id, role);

            CREATE TABLE IF NOT EXISTS core.sample_facts (
                asset_id varchar(64) NOT NULL,
                signal_id varchar(128) NOT NULL,
                time_utc timestamptz NOT NULL,
                value double precision NULL,
                quality smallint NOT NULL DEFAULT 0,
                PRIMARY KEY (asset_id, signal_id, time_utc)
            );

            CREATE INDEX IF NOT EXISTS ix_sample_facts_asset_time
                ON core.sample_facts (asset_id, time_utc);

            CREATE TABLE IF NOT EXISTS core.classified_events (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                owner_org_id varchar(64) NOT NULL,
                plant_id varchar(64) NOT NULL,
                start_utc timestamptz NOT NULL,
                end_utc timestamptz NULL,
                state varchar(32) NOT NULL,
                cause_code varchar(64) NULL,
                confidence double precision NOT NULL DEFAULT 0,
                sources_json jsonb NOT NULL DEFAULT '[]'::jsonb,
                rationale varchar(2000) NULL
            );

            CREATE INDEX IF NOT EXISTS ix_classified_events_plant_start
                ON core.classified_events (plant_id, start_utc);
            """;

        try
        {
            await db.Database.ExecuteSqlRawAsync(baseTablesSql, ct).ConfigureAwait(false);
            logger.LogDebug("SCADA schema ensured (signal_map, sample_facts, classified_events).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kunne ikke sikre SCADA-skjemaet. Fortsetter uten det.");
            return;
        }

        // 2) Hvis TimescaleDB er på plass, gjør sample_facts og classified_events
        //    om til hypertables. Idempotent via if_not_exists-flagget.
        if (!hasTimescale)
        {
            return;
        }

        const string hypertableSql = """
            SELECT create_hypertable(
                'core.sample_facts', 'time_utc',
                chunk_time_interval => INTERVAL '7 days',
                if_not_exists => TRUE,
                migrate_data => TRUE
            );

            SELECT create_hypertable(
                'core.classified_events', 'start_utc',
                chunk_time_interval => INTERVAL '30 days',
                if_not_exists => TRUE,
                migrate_data => TRUE
            );
            """;

        try
        {
            await db.Database.ExecuteSqlRawAsync(hypertableSql, ct).ConfigureAwait(false);
            logger.LogInformation("Hypertables konfigurert for sample_facts og classified_events.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kunne ikke konvertere til hypertables — fortsetter med vanlige tabeller.");
        }
    }

    /// <summary>
    /// Idempotent skjema-bro for kaskade-modellen (Spec KASKADE-DAMMER):
    ///   - <c>core.dams</c> per-anlegg dam-katalog med <c>is_turbine_intake</c>-markør
    ///   - <c>core.signal_map.dam_id</c> kolonne (nullable)
    ///
    /// Backfill: alle eksisterende anlegg uten dam får én default-dam med
    /// dam_id = '{plant}_main' og is_turbine_intake = true. Dam-relaterte
    /// signal_map-rader oppdateres med samme DamId. Generator-relaterte rader
    /// (GeneratorActivePower, TurbineWaterFlow osv.) beholder dam_id = NULL.
    ///
    /// Idempotent: kjører på nytt uten å duplisere dammer eller endre eksisterende
    /// IsTurbineIntake-tilordning.
    /// </summary>
    private static async Task EnsureDamsSchemaAsync(
        KraftverkDbContext db, ILogger logger, CancellationToken ct)
    {
        // Tabellen + deferrable unique-constraint (én terminal-dam per plant).
        // Constraint er deferrable for å la PlantAdmin-UI flytte intake-markøren
        // mellom dammer i én transaksjon.
        const string ddlSql = """
            CREATE TABLE IF NOT EXISTS core.dams (
                plant_id varchar(64) NOT NULL,
                dam_id varchar(64) NOT NULL,
                name varchar(128) NOT NULL,
                cascade_position integer NOT NULL DEFAULT 1,
                is_turbine_intake boolean NOT NULL DEFAULT FALSE,
                hrv_moh double precision NULL,
                lrv_moh double precision NULL,
                volume_mm3 double precision NULL,
                created_at_utc timestamptz NOT NULL DEFAULT NOW(),
                owner_org_id varchar(64) NOT NULL,
                PRIMARY KEY (plant_id, dam_id)
            );

            -- Partiell unique-index: kun én terminal-dam per plant (rader med
            -- is_turbine_intake = false får ikke noen begrensning).
            CREATE UNIQUE INDEX IF NOT EXISTS ux_dams_one_intake_per_plant
                ON core.dams (plant_id) WHERE is_turbine_intake = TRUE;

            CREATE INDEX IF NOT EXISTS ix_dams_plant
                ON core.dams (plant_id);

            CREATE INDEX IF NOT EXISTS ix_dams_plant_intake
                ON core.dams (plant_id, is_turbine_intake);

            ALTER TABLE core.signal_map
                ADD COLUMN IF NOT EXISTS dam_id varchar(64) NULL;

            CREATE INDEX IF NOT EXISTS ix_signal_map_dam
                ON core.signal_map (plant_id, dam_id, role);
            """;

        try
        {
            await db.Database.ExecuteSqlRawAsync(ddlSql, ct).ConfigureAwait(false);
            logger.LogDebug("Dams-skjema sikret (core.dams + signal_map.dam_id).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kunne ikke sikre dams-skjemaet — fortsetter uten det.");
            return;
        }

        // Backfill: alle anlegg uten dam får én default-dam markert som terminal.
        // SQL er idempotent — INSERT ... WHERE NOT EXISTS hopper over allerede-seedete plants.
        // Bruker plants.id (ikke plants.plant_id — kolonnen heter 'id' i schemaet).
        const string backfillDamsSql = """
            INSERT INTO core.dams
                (plant_id, dam_id, name, cascade_position, is_turbine_intake, owner_org_id)
            SELECT
                p.id AS plant_id,
                p.id || '_main' AS dam_id,
                p.name AS name,
                1 AS cascade_position,
                TRUE AS is_turbine_intake,
                p.owner_org_id
            FROM core.plants p
            WHERE NOT EXISTS (
                SELECT 1 FROM core.dams d WHERE d.plant_id = p.id
            );
            """;

        // Dam-relaterte roller får dam_id = '<plant>_main' for eksisterende rader.
        // Generator-relaterte roller forblir NULL.
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

        try
        {
            var damsAdded = await db.Database.ExecuteSqlRawAsync(backfillDamsSql, ct).ConfigureAwait(false);
            var sigsUpdated = await db.Database.ExecuteSqlRawAsync(backfillSignalMapSql, ct).ConfigureAwait(false);
            if (damsAdded > 0 || sigsUpdated > 0)
            {
                logger.LogInformation(
                    "Dams-backfill: {Dams} default-dammer opprettet, {Signals} signal_map-rader fikk DamId.",
                    damsAdded, sigsUpdated);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kunne ikke kjøre dam-backfill — eksisterende plants kan mangle terminal-dam.");
        }
    }

    /// <summary>
    /// Idempotent skjema-bro for SPEC-IMPORT-COMPLETENESS:
    ///   - <c>core.data_source_expectations</c>: per anlegg per kilde-type
    ///   - <c>core.data_imports</c>: logg over faktiske importer
    ///   - Backfill: alle eksisterende plants får settlement + hydrogrid_plan-rader
    ///     (de to kildene som er aktive i dag for hele porteføljen). SCADA og
    ///     operlog seedes ikke automatisk — drifts-leder aktiverer per anlegg
    ///     via PlantAdmin når flow er på plass.
    /// </summary>
    private static async Task EnsureDataCompletenessSchemaAsync(
        KraftverkDbContext db, ILogger logger, CancellationToken ct)
    {
        const string ddlSql = """
            CREATE TABLE IF NOT EXISTS core.data_source_expectations (
                plant_id varchar(64) NOT NULL,
                source_type varchar(32) NOT NULL,
                cadence varchar(16) NOT NULL DEFAULT 'monthly',
                expected_lag_days integer NOT NULL DEFAULT 7,
                is_active boolean NOT NULL DEFAULT TRUE,
                activated_at_utc timestamptz NULL,
                deactivated_at_utc timestamptz NULL,
                PRIMARY KEY (plant_id, source_type)
            );

            -- Idempotent: legg til ny kolonne for konfigurerbar dekningsterskel
            ALTER TABLE core.data_source_expectations
                ADD COLUMN IF NOT EXISTS completion_threshold_pct double precision NOT NULL DEFAULT 0.95;

            CREATE TABLE IF NOT EXISTS core.data_imports (
                import_id uuid PRIMARY KEY,
                plant_id varchar(64) NOT NULL,
                source_type varchar(32) NOT NULL,
                period_from_utc timestamptz NOT NULL,
                period_to_utc timestamptz NOT NULL,
                imported_at_utc timestamptz NOT NULL DEFAULT NOW(),
                file_name varchar(255) NULL,
                file_hash varchar(64) NULL,
                rows_imported integer NULL,
                coverage_pct double precision NULL,
                user_id varchar(128) NULL,
                notes text NULL
            );

            CREATE INDEX IF NOT EXISTS ix_imports_plant_source_period
                ON core.data_imports (plant_id, source_type, period_from_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_imports_imported_at
                ON core.data_imports (imported_at_utc);
            """;

        try
        {
            await db.Database.ExecuteSqlRawAsync(ddlSql, ct).ConfigureAwait(false);
            logger.LogDebug("Data-completeness-skjema sikret (data_source_expectations + data_imports).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kunne ikke sikre data-completeness-skjemaet — fortsetter uten det.");
            return;
        }

        // Backfill: alle plants får settlement (KAIA-eksport, lag 7 dager).
        // Settlement-fila inneholder også Hydrogrid-plan-kolonnen, så vi
        // sporer ikke det som egen kilde-type. SCADA-trender og SCADA-alarmer
        // seedes IKKE her — drifts-leder aktiverer per anlegg via PlantAdmin
        // når eksport-flow er på plass.
        //
        // Drifts-leders bekreftelse 2026-05-03: kun 3 datakilder eksisterer
        // — settlement (KAIA), scada_trends (master-CSV), scada_alarms (operlog).
        // Idempotent: ON CONFLICT DO NOTHING bevarer eksisterende konfig.
        const string backfillSql = """
            INSERT INTO core.data_source_expectations
                (plant_id, source_type, cadence, expected_lag_days, is_active, activated_at_utc)
            SELECT p.id, 'settlement', 'monthly', 7, TRUE, '2024-01-01'::timestamptz
            FROM core.plants p
            ON CONFLICT (plant_id, source_type) DO NOTHING;

            -- Rydd bort tidligere seedet hydrogrid_plan-rader (de er nå
            -- konsolidert inn i settlement). Idempotent — ingen-op hvis
            -- ingen slike rader eksisterer.
            DELETE FROM core.data_source_expectations
            WHERE source_type = 'hydrogrid_plan';
            """;

        try
        {
            var added = await db.Database.ExecuteSqlRawAsync(backfillSql, ct).ConfigureAwait(false);
            if (added > 0)
            {
                logger.LogInformation(
                    "Data-completeness backfill: {Added} expectation-rader seeded (settlement + hydrogrid_plan per plant).",
                    added);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kunne ikke kjøre data-completeness backfill — manglende expectations.");
        }
    }

    /// <summary>
    /// Idempotent skjema-bro for capture rate (Spec CAPTURE-RATE):
    ///  - <c>core.market_prices</c> for spotpris-baseline per (prisområde, time)
    ///  - <c>core.plants.price_area</c> kolonne (default NO2) for å koble plant til prisområde
    /// </summary>
    private static async Task EnsureMarketPricesSchemaAsync(
        KraftverkDbContext db, ILogger logger, CancellationToken ct)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS core.market_prices (
                price_area varchar(8) NOT NULL,
                time_utc timestamptz NOT NULL,
                price_nok_mwh double precision NOT NULL,
                source varchar(16) NOT NULL,
                recorded_at_utc timestamptz NOT NULL DEFAULT NOW(),
                PRIMARY KEY (price_area, time_utc)
            );

            CREATE INDEX IF NOT EXISTS ix_market_prices_time
                ON core.market_prices (time_utc);

            ALTER TABLE core.plants
                ADD COLUMN IF NOT EXISTS price_area varchar(8) NOT NULL DEFAULT 'NO2';
            """;

        try
        {
            await db.Database.ExecuteSqlRawAsync(sql, ct).ConfigureAwait(false);
            logger.LogDebug("MarketPrices-skjema sikret (market_prices + plants.price_area).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Kunne ikke sikre market_prices-skjemaet — fortsetter uten det.");
        }
    }
}
