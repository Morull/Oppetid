using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Seeder for Grødemfoss' 20-tags whitelist (export-20-tags-avg-hour-...csv).
/// Kun G2 produserer (G1 havarert iflg drifts-leder 2026-05-05) — alle
/// generator-tags har derfor G2-prefiks. Topologi er enkelt: 1 dam (Smievatn,
/// terminal). Dam-id er <c>grodemfoss_main</c> (oppdatert med navnet "Smievatn"
/// av <see cref="PlantTopologySeeder"/>).
///
/// Idempotent — hopper over tags som allerede finnes i signal_map for plant 'grodemfoss'.
/// </summary>
public static class GrodemfossSignalMapSeeder
{
    private const string PlantId = "grodemfoss";
    private const string OwnerOrgId = "dev-org";
    private const string MainDamId = "grodemfoss_main";

    private static readonly (string SignalId, string CsvColumn, string Unit, SignalRole Role)[] Mappings =
    [
        // Generator G2 (G1 retired)
        ("GRODEM_G2_GEN_P_PV",                          "Cluster1.GRODEM_G2_GEN_P_PV",                          "kW",   SignalRole.GeneratorActivePower),
        ("GRODEM_G2_GEN_Q_PV",                          "Cluster1.GRODEM_G2_GEN_Q_PV",                          "kVAr", SignalRole.ElectricalMeasurement),
        ("GRODEM_G2_TURB_VF_PV",                        "Cluster1.GRODEM_G2_TURB_VF_PV",                        "m3/s", SignalRole.TurbineWaterFlow),
        ("GRODEM_G2_TURB_VIRKNGRD_PV",                  "Cluster1.GRODEM_G2_TURB_VIRKNGRD_PV",                  "%",    SignalRole.TurbineEfficiency),
        ("GRODEM_G2_RORGATE_VANN_TRYKK_PV",             "Cluster1.GRODEM_G2_RORGATE_VANN_TRYKK_PV",             "mKVs", SignalRole.HydraulicPressure),

        // Lager-temp + regulator/AGC — whitelist-only (Other), brukes ikke i KPI-er
        ("GRODEM_G2_GEN_AKSLAGER_TSDE_TEMP_PV",         "Cluster1.GRODEM_G2_GEN_AKSLAGER_TSDE_TEMP_PV",         "°C",   SignalRole.Other),
        ("GRODEM_G2_GEN_RADLAGER_DE_TEMP_PV",           "Cluster1.GRODEM_G2_GEN_RADLAGER_DE_TEMP_PV",           "°C",   SignalRole.Other),
        ("GRODEM_G2_GEN_RADLAGER_NDE_TEMP_PV",          "Cluster1.GRODEM_G2_GEN_RADLAGER_NDE_TEMP_PV",          "°C",   SignalRole.Other),
        ("GRODEM_G2_KONTROLL_AGC_DB_SP",                "Cluster1.GRODEM_G2_KONTROLL_AGC_DB_SP",                "None", SignalRole.Other),
        ("GRODEM_G2_KONTROLL_REG_P_SP_SP_LAST",         "Cluster1.GRODEM_G2_KONTROLL_REG_P_SP_SP_LAST",         "None", SignalRole.Other),
        ("GRODEM_G2_KONTROLL_REG_P_SP_TM_PV",           "Cluster1.GRODEM_G2_KONTROLL_REG_P_SP_TM_PV",           "None", SignalRole.Other),

        // KRST: kontroll-/kommunikasjons-stasjon
        ("GRODEM_KRST_KONTROLL_KOM_AL",                 "Cluster1.GRODEM_KRST_KONTROLL_KOM_AL",                 "None", SignalRole.CommunicationAlarm),

        // Smievatn (terminal-dam, dam_id=grodemfoss_main)
        ("GRODEM_SMIEVT_KONTROLL_MAG_FYLLGRD_PV",       "Cluster1.GRODEM_SMIEVT_KONTROLL_MAG_FYLLGRD_PV",       "%",      SignalRole.ReservoirFillFactor),
        ("GRODEM_SMIEVT_KONTROLL_MAG_NEDBKAP_PV",       "Cluster1.GRODEM_SMIEVT_KONTROLL_MAG_NEDBKAP_PV",       "mm",     SignalRole.LowestRegulatedLevel),
        ("GRODEM_SMIEVT_KONTROLL_MAG_OVLOP_PV",         "Cluster1.GRODEM_SMIEVT_KONTROLL_MAG_OVLOP_PV",         "m3/s",   SignalRole.OverflowFlow),
        ("GRODEM_SMIEVT_KONTROLL_MAG_VOLUM_PV",         "Cluster1.GRODEM_SMIEVT_KONTROLL_MAG_VOLUM_PV",         "Mill.m3", SignalRole.ReservoirVolume),
        ("GRODEM_SMIEVT_KONTROLL_TOT_VF_PV",            "Cluster1.GRODEM_SMIEVT_KONTROLL_TOT_VF_PV",            "m3/s",   SignalRole.TotalDamFlow),
        ("GRODEM_SMIEVT_NIVA_SENSOR_PRI_HRV_PV",        "Cluster1.GRODEM_SMIEVT_NIVA_SENSOR_PRI_HRV_PV",        "cm",     SignalRole.UpstreamLevel),
        ("GRODEM_SMIEVT_NIVA_SENSOR_PRI_KOTE_PV",       "Cluster1.GRODEM_SMIEVT_NIVA_SENSOR_PRI_KOTE_PV",       "moh",    SignalRole.UpstreamLevel),
        ("GRODEM_SMIEVT_NIVA_VTA_PV",                   "Cluster1.GRODEM_SMIEVT_NIVA_VTA_PV",                   "None",   SignalRole.Other),
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GrodemfossSignalMapSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            var existing = await db.SignalMaps
                .Where(x => x.PlantId == PlantId)
                .Select(x => x.SignalId)
                .ToListAsync(ct).ConfigureAwait(false);
            var existingSet = existing.ToHashSet(StringComparer.Ordinal);

            var added = 0;
            foreach (var (signalId, csvCol, unit, role) in Mappings)
            {
                if (existingSet.Contains(signalId)) continue;
                db.SignalMaps.Add(new SignalMapEntry
                {
                    PlantId = PlantId,
                    SignalId = signalId,
                    CsvColumn = csvCol,
                    Unit = unit,
                    Role = role,
                    StoreSamples = true,
                    IsActive = true,
                    OwnerOrgId = OwnerOrgId,
                    DamId = IsDamRelated(role) ? MainDamId : null,
                });
                added++;
            }

            if (added > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                logger.LogInformation("Grødemfoss: seedet {Count} signal_map-rader.", added);
            }
            else
            {
                logger.LogDebug("Grødemfoss signal-map already present, skipping seed.");
            }
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            logger.LogWarning(
                "Grødemfoss-signal-seed: 'core.signal_map' finnes ikke ennå. " +
                "Restart etter at SCADA-skjemaet er på plass.");
        }
    }

    private static bool IsDamRelated(SignalRole role) => role switch
    {
        SignalRole.OverflowFlow => true,
        SignalRole.UpstreamLevel => true,
        SignalRole.DownstreamLevel => true,
        SignalRole.ReservoirFillFactor => true,
        SignalRole.LowestRegulatedLevel => true,
        SignalRole.GateFlow => true,
        SignalRole.GatePosition => true,
        SignalRole.TotalDamFlow => true,
        SignalRole.ReservoirVolume => true,
        _ => false,
    };

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
