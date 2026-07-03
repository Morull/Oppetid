using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Anvender den konsoliderte 15-min-eksportens aktive tag-liste
/// (<c>signalliste_eksport_15min.csv</c>, repo-rot, driftsleder 2026-07-02)
/// på <c>core.signal_map</c> — SPEC-IMPORT-KONSOLIDERT-15MIN Endring D:
/// <list type="bullet">
/// <item>Tags i fasiten: <c>IsActive=true, StoreSamples=true</c>.</item>
/// <item>Alle andre tags (10 anlegg + Liavatn): <c>IsActive=false,
/// StoreSamples=false</c> — radene og historiske samples SLETTES IKKE, så
/// KPI-er bakover i tid er uendret. Import-filteret i
/// <c>ScadaImportService</c> dropper samples for deaktiverte/ukjente tags.</item>
/// <item>Liavatn seedes med sine 9 KPI-tags (fra faktiske sample_facts-tags —
/// anlegget manglet tag-katalog i koden; TurbineEfficiency/TotalDamFlow
/// finnes ikke i Liavatn-eksporten).</item>
/// <item>Øvre kaskade-dammer (uten tags i eksporten) settes
/// <c>is_active=false</c> så UI ikke viser tomme dammer.</item>
/// </list>
///
/// KJØRES ÉN GANG, styrt av markør i <c>core.seed_markers</c>: driftsleders
/// senere manuelle endringer (re-aktivering av en tag via signal-maps-API-et)
/// skal ikke overskrives ved neste oppstart. Ny fasit-versjon → nytt
/// markør-navn.
/// </summary>
public static class AktivTagListeSeeder
{
    private const string MarkerName = "aktiv-tagliste-2026-07-02";

    /// <summary>
    /// Aktive tags per fasit (signalliste_eksport_15min.csv rader med
    /// status=aktiv) + Liavatn-utfyllingen. Kun signal_id — plant-tilhørighet
    /// er entydig via tag-prefiks, og signal_map-PK er (plant_id, signal_id).
    /// </summary>
    internal static readonly string[] AktiveTags =
    [
        // drivdal (8)
        "DRIVDAL_G1_GEN_P_PV", "DRIVDAL_G1_TURB_VF_PV", "DRIVDAL_G1_TURB_VIRKNGRD_PV",
        "DRIVDAL_INNTAK_NIVA_OVERLOP_VF_PV", "DRIVDAL_INNTAK_MAGASIN_FYLLGRAD_PV",
        "DRIVDAL_INNTAK_NIVA_OPPSTROM_KOTE_PV", "DRIVDAL_INNTAK_MAGASIN_NED_KAP_PV",
        "DRIVDAL_KRST_KONTROLL_KOM_AL",
        // grodemfoss (11) — HRV-varianten beholdes aktiv i tillegg til fasitens
        // KOTE: den er dagens alfabetiske førstevalg for UpstreamLevel på
        // terminal-dammen (KPI-bevaring, se kommentar under LiavatnTags).
        "GRODEM_G2_GEN_P_PV", "GRODEM_G2_TURB_VF_PV", "GRODEM_G2_TURB_VIRKNGRD_PV",
        "GRODEM_SMIEVT_KONTROLL_MAG_OVLOP_PV", "GRODEM_SMIEVT_KONTROLL_MAG_VOLUM_PV",
        "GRODEM_SMIEVT_KONTROLL_TOT_VF_PV", "GRODEM_SMIEVT_KONTROLL_MAG_FYLLGRD_PV",
        "GRODEM_SMIEVT_NIVA_SENSOR_PRI_KOTE_PV", "GRODEM_SMIEVT_NIVA_SENSOR_PRI_HRV_PV",
        "GRODEM_SMIEVT_KONTROLL_MAG_NEDBKAP_PV",
        "GRODEM_KRST_KONTROLL_KOM_AL",
        // honnefoss (9)
        "HONNE_G1_GEN_P_PV", "HONNE_G1_TURB_VF_PV", "HONNE_G1_TURB_VIRKNGRD_PV",
        "HONNE_INNTAK_NIVA_OVERLOP_VF_PV", "HONNE_INNTAK_MAGASIN_VOLUM_PV",
        "HONNE_INNTAK_MAGASIN_TOT_VF_PV", "HONNE_INNTAK_MAGASIN_FYLLGRAD_PV",
        "HONNE_INNTAK_MAGASIN_NED_KAP_PV", "HONNE_KRST_KONTROLL_KOM_AL",
        // lindland (13) — spec pkt. 5.5 sa «behold NIVA_OVERLOP_VF, deaktiver
        // alt-mappingen — med mindre datakvalitetssjekk viser at den andre er
        // bedre». Sjekken (2026-07-03) viste at NIVA_OVERLOP_VF er KONSTANT
        // 0,00 hele perioden (død/feilkalibrert sensor) mens KONTROLL_MAG_OVLOP
        // har 243 timer reelt spill — så BEGGE beholdes aktive: alt-taggen
        // bærer historikken (deaktivering ville slettet Lindlands overløp fra
        // Vakt-ROI), primær-taggen er den eksporten sender fremover.
        "LINDLAND_G1_GEN_P_PV", "LINDLAND_G2_GEN_P_PV",
        "LINDLAND_G1_TURB_VF_PV", "LINDLAND_G2_TURB_VF_PV",
        "LINDLAND_G1_TURB_VIRKNGRD_PV", "LINDLAND_G2_TURB_VIRKNGRD_PV",
        "LINDLAND_INNTAK_NIVA_OVERLOP_VF_PV", "LINDLAND_INNTAK_KONTROLL_MAG_OVLOP_PV",
        "LINDLAND_INNTAK_KONTROLL_MAG_VOLUM_PV",
        "LINDLAND_INNTAK_KONTROLL_TOT_VF_PV", "LINDLAND_INNTAK_KONTROLL_MAG_FYLLGRD_PV",
        "LINDLAND_INNTAK_NIVA_OPPSTROM_KOTE_PV", "LINDLAND_KRST_KONTROLL_KOM_AL",
        // haukland (11) — INNTAK_KOTE beholdes aktiv i tillegg til fasitens
        // STEMMEVT_MOH: dagens alfabetiske førstevalg for UpstreamLevel på
        // terminal-dammen (KPI-bevaring).
        "HAUKLAND_G1_GEN_P_PV", "HAUKLAND_G1_TURB_VF_PV", "HAUKLAND_G1_TURB_VIRKNGRD_PV",
        "HAUKLAND_STEMMEVT_KONTROLL_MAG_OVLOP_PV", "HAUKLAND_STEMMEVT_KONTROLL_MAG_VOLUM_PV",
        "HAUKLAND_STEMMEVT_KONTROLL_TOT_VF_PV", "HAUKLAND_STEMMEVT_KONTROLL_MAG_FYLLGRD_PV",
        "HAUKLAND_STEMMEVT_NIVA_MOH_PV", "HAUKLAND_INNTAK_NIVA_OPPSTROM_KOTE_PV",
        "HAUKLAND_STEMMEVT_KONTROLL_MAG_NEDBKAP_PV",
        "HAUKLAND_KRST_KONTROLL_KOM_AL",
        // vikesa (9)
        "VIKESA_G1_GEN_P_PV", "VIKESA_G1_TURB_VF_PV", "VIKESA_G1_TURB_VIRKNGRD_PV",
        "VIKESA_INNTAK_NIVA_OVERLOP_VF_PV", "VIKESA_INNTAK_MAGASIN_VOLUM_PV",
        "VIKESA_INNTAK_MAGASIN_FYLLGRAD_PV", "VIKESA_INNTAK_NIVA_OPPSTROM_REF_HRV_PV",
        "VIKESA_INNTAK_MAGASIN_NED_KAP_PV", "VIKESA_KRST_KONTROLL_KOM_AL",
        // ogreyfoss (14)
        "OGREY1_G1_GEN_P_PV", "OGREY1_G1_TURB_VF_PV", "OGREY1_G1_TURB_VIRKNGRD_PV",
        "OGREY1_INNTAK_NIVA_OVERLOP_VF_PV", "OGREY1_INNTAK_MAGASIN_VOLUM_PV",
        "OGREY1_INNTAK_MAGASIN_TOT_VF_PV", "OGREY1_INNTAK_MAGASIN_FYLLGRAD_PV",
        "OGREY1_INNTAK_NIVA_OPPSTROM_KOTE_PV", "OGREY1_INNTAK_MAGASIN_NED_KAP_PV",
        "OGREY1_KRST_KONTROLL_KOM_AL",
        "OGREY2_G2_GEN_P_PV", "OGREY2_G2_TURB_VF_PV", "OGREY2_G2_TURB_VIRKNGRD_PV",
        "OGREY2_KRST_KONTROLL_KOM_AL",
        // logjen (10)
        "LOGJEN_G1_GEN_P_PV", "LOGJEN_G1_TURB_VF_PV", "LOGJEN_G1_TURB_VIRKNGRD_PV",
        "LOGJEN_INNTAK_NIVA_OVERLOP_VF_PV", "LOGJEN_INNTAK_MAGASIN_VOLUM_PV",
        "LOGJEN_INNTAK_MAGASIN_TOT_VF_PV", "LOGJEN_INNTAK_MAGASIN_FYLLGRAD_PV",
        "LOGJEN_INNTAK_NIVA_OPPSTROM_REF_HRV_PV", "LOGJEN_INNTAK_MAGASIN_NED_KAP_PV",
        "LOGJEN_KRST_KONTROLL_KOM_AL",
        // stolskraft (2) — overløp/virkningsgrad/kom-alarm MANGLER I SCADA-
        // EKSPORT (nye tags må opprettes i SCADA; overløp dekkes imens av
        // ProductionStateProxy).
        "STOLSKRAFT_G1_GEN_P_PV", "STOLSKRAFT_G1_TURB_VF_PV",
        // orsdalen (3) — overløp = ProductionStateProxy (kun GEN_P);
        // effektivitetsroller bevisst utelatt (elvekraft uten magasin).
        "ORSDAL_G1_GEN_P_PV", "ORSDAL_INNTAK_NIVA_OPPSTROM_KOTE_PV",
        "ORSDAL_KRST_KONTROLL_KOM_AL",
        // liavatn (9) — utfylt fra faktiske sample_facts-tags (fasit-raden
        // sto som AVKLARES). TurbineEfficiency/TotalDamFlow finnes ikke.
        "LIAVATN_G1_GEN_P_PV", "LIAVATN_G1_TURB_VF_PV",
        "LIAVATN_VDAL_INNTAK_NIVA_OVERLOP_VF_PV", "LIAVATN_INNTAK_MAGASIN_VOLUM_PV",
        "LIAVATN_INNTAK_MAGASIN_FYLLGRAD_PV", "LIAVATN_INNTAK_NIVA_OPPSTROM_REF_HRV_PV",
        "LIAVATN_INNTAK_MAGASIN_NED_KAP_PV", "LIAVATN_KRST_KONTROLL_KOM_AL",
        "LIAVATN_KRST_NIVA_UTLOP_PV",
    ];

    /// <summary>
    /// Liavatn-tags som seedes inn i signal_map (anlegget manglet katalog).
    /// (signal_id, role, unit, dam_id). Roller speiler fasit-mønsteret.
    /// </summary>
    private static readonly (string SignalId, string Role, string Unit, string? DamId)[] LiavatnTags =
    [
        ("LIAVATN_G1_GEN_P_PV", "GeneratorActivePower", "kW", null),
        ("LIAVATN_G1_TURB_VF_PV", "TurbineWaterFlow", "m3/s", null),
        ("LIAVATN_VDAL_INNTAK_NIVA_OVERLOP_VF_PV", "OverflowFlow", "m3/s", "liavatn_main"),
        ("LIAVATN_INNTAK_MAGASIN_VOLUM_PV", "ReservoirVolume", "Mill.m3", "liavatn_main"),
        ("LIAVATN_INNTAK_MAGASIN_FYLLGRAD_PV", "ReservoirFillFactor", "%", "liavatn_main"),
        ("LIAVATN_INNTAK_NIVA_OPPSTROM_REF_HRV_PV", "UpstreamLevel", "cm", "liavatn_main"),
        ("LIAVATN_INNTAK_MAGASIN_NED_KAP_PV", "LowestRegulatedLevel", "mm", "liavatn_main"),
        ("LIAVATN_KRST_KONTROLL_KOM_AL", "CommunicationAlarm", "", null),
        ("LIAVATN_KRST_NIVA_UTLOP_PV", "DownstreamLevel", "moh", null),
    ];

    // KPI-bevarende unntak utover fasiten er lagt DIREKTE i AktiveTags over
    // (Lindland alt-overløp, Grødemfoss HRV-nivå, Haukland INNTAK-kote):
    // rolle-oppslagene GetSignalIdForRole/GetByPlantDamAndRole filtrerer på
    // IsActive og velger alfabetisk først — å deaktivere dagens «vinner» på
    // terminal-dammen ville byttet signal for HISTORISKE spørringer og brutt
    // akseptansekriteriet om uendrede KPI-tall (§8).

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("AktivTagListeSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            // Markør-tabell (engangs-migrasjoner som IKKE skal re-anvendes
            // over driftsleders manuelle endringer).
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS core.seed_markers (
                    name varchar(128) PRIMARY KEY,
                    applied_at_utc timestamptz NOT NULL DEFAULT NOW()
                );
                """, ct).ConfigureAwait(false);

            var alreadyApplied = await db.Database
                .SqlQueryRaw<int>(
                    "SELECT 1 AS \"Value\" FROM core.seed_markers WHERE name = {0}", MarkerName)
                .AnyAsync(ct).ConfigureAwait(false);
            if (alreadyApplied) return;

            // 1. Liavatn: seed manglende signal_map-rader (idempotent).
            // NULLIF-sentinel for dam-løse tags: EF raw-SQL-parametere støtter
            // verken null eller DBNull direkte.
            foreach (var (signalId, role, unit, damId) in LiavatnTags)
            {
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO core.signal_map
                        (plant_id, signal_id, csv_column, unit, role, store_samples,
                         is_active, dam_id, owner_org_id, created_at, updated_at)
                    VALUES ('liavatn', {0}, {1}, {2}, {3}, TRUE, TRUE, NULLIF({4}, ''), 'dev-org', NOW(), NOW())
                    ON CONFLICT (plant_id, signal_id) DO NOTHING;
                    """,
                    new object[] { signalId, $"Cluster1.{signalId}", unit, role, damId ?? "" }, ct)
                    .ConfigureAwait(false);
            }

            // 2. Deaktiver alt som ikke står i fasiten; aktiver fasiten.
            // Signal-id-ene er kompileringstids-konstanter (ingen injection),
            // men sendes likevel via ANY(array-parameter) for ryddighet.
            var deactivated = await db.Database.ExecuteSqlRawAsync("""
                UPDATE core.signal_map
                SET is_active = FALSE, store_samples = FALSE, updated_at = NOW()
                WHERE NOT (signal_id = ANY({0}))
                  AND (is_active = TRUE OR store_samples = TRUE);
                """, new object[] { AktiveTags }, ct).ConfigureAwait(false);

            var activated = await db.Database.ExecuteSqlRawAsync("""
                UPDATE core.signal_map
                SET is_active = TRUE, store_samples = TRUE, updated_at = NOW()
                WHERE signal_id = ANY({0})
                  AND (is_active = FALSE OR store_samples = FALSE);
                """, new object[] { AktiveTags }, ct).ConfigureAwait(false);

            // 3. Deaktiver øvre kaskade-dammer (ingen tags i eksporten —
            // kun inntak/terminal-dammen eksporteres).
            var damsDeactivated = await db.Database.ExecuteSqlRawAsync("""
                UPDATE core.dams
                SET is_active = FALSE
                WHERE is_turbine_intake = FALSE;
                """, ct).ConfigureAwait(false);

            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO core.seed_markers (name) VALUES ({0}) ON CONFLICT DO NOTHING;",
                new object[] { MarkerName }, ct).ConfigureAwait(false);

            logger.LogInformation(
                "AktivTagListe anvendt ({Marker}): {Aktive} tags aktive, {Deaktivert} tag-rader "
                + "deaktivert, {Dams} øvre kaskade-dammer deaktivert, {Reaktivert} re-aktivert. "
                + "Historiske samples er urørt.",
                MarkerName, AktiveTags.Length, deactivated, damsDeactivated, activated);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "AktivTagListeSeeder feilet — tag-minimering ikke anvendt; import-filteret "
                + "faller tilbake til eksisterende signal_map-flagg.");
        }
    }
}
