using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Seeder for Drivdals 22-tags whitelist (eksport-22-tags-avg-hour-...csv).
/// Mapper hver SCADA-tag til en <see cref="SignalRole"/> slik at klassifikator
/// og virkningsgrad-beregner kan slå opp tags ved rolle istedenfor ved navn.
///
/// Idempotent — hopper over tags som allerede finnes i signal_map for
/// <c>plant_id = 'drivdal'</c>.
/// </summary>
public static class DrivdalSignalMapSeeder
{
    private const string PlantId = "drivdal";
    private const string OwnerOrgId = "dev-org";
    /// <summary>
    /// Drivdal er ett-dam-anlegg. Default-dammen som backfill oppretter heter
    /// <c>drivdal_main</c> og er terminal-dam (IsTurbineIntake=true).
    /// Dam-relaterte signaler får denne DamId-en; generator/turbin-tags er null.
    /// </summary>
    private const string MainDamId = "drivdal_main";

    private static readonly (string SignalId, string CsvColumn, string Unit, SignalRole Role)[] Mappings =
    [
        ("DRIVDAL_G1_GEN_P_PV",                     "Cluster1.DRIVDAL_G1_GEN_P_PV",                     "kW",      SignalRole.GeneratorActivePower),
        ("DRIVDAL_G1_GEN_TURTALL_PV",               "Cluster1.DRIVDAL_G1_GEN_TURTALL_PV",               "rpm",     SignalRole.GeneratorRpm),
        ("DRIVDAL_G1_GEN_COSPHI_PV",                "Cluster1.DRIVDAL_G1_GEN_COSPHI_PV",                "None",    SignalRole.ElectricalMeasurement),
        ("DRIVDAL_G1_GEN_Q_PV",                     "Cluster1.DRIVDAL_G1_GEN_Q_PV",                     "KVar",    SignalRole.ElectricalMeasurement),
        ("DRIVDAL_G1_GEN_S_PV",                     "Cluster1.DRIVDAL_G1_GEN_S_PV",                     "KVa",     SignalRole.ElectricalMeasurement),
        ("DRIVDAL_G1_GEN_TIMETELLER_PV",            "Cluster1.DRIVDAL_G1_GEN_TIMETELLER_PV",            "t",       SignalRole.Other),

        ("DRIVDAL_G1_TURB_VF_PV",                   "Cluster1.DRIVDAL_G1_TURB_VF_PV",                   "m3/s",    SignalRole.TurbineWaterFlow),
        ("DRIVDAL_G1_TURB_VIRKNGRD_PV",             "Cluster1.DRIVDAL_G1_TURB_VIRKNGRD_PV",             "%",       SignalRole.TurbineEfficiency),
        ("DRIVDAL_G1_TURB_PADRAG_PV",               "Cluster1.DRIVDAL_G1_TURB_PADRAG_PV",               "%",       SignalRole.TurbinePadrag),

        ("DRIVDAL_G1_RORGATE_VANN_TRYKK_PV",        "Cluster1.DRIVDAL_G1_RORGATE_VANN_TRYKK_PV",        "mKVs",    SignalRole.HydraulicPressure),
        ("DRIVDAL_G1_HYDRL_OLJE_TRYKK_PV",          "Cluster1.DRIVDAL_G1_HYDRL_OLJE_TRYKK_PV",          "bar",     SignalRole.HydraulicPressure),

        ("DRIVDAL_INNTAK_NIVA_OPPSTROM_KOTE_PV",    "Cluster1.DRIVDAL_INNTAK_NIVA_OPPSTROM_KOTE_PV",    "moh",     SignalRole.UpstreamLevel),
        ("DRIVDAL_INNTAK_NIVA_OPPSTROM_REF_HRV_PV", "Cluster1.DRIVDAL_INNTAK_NIVA_OPPSTROM_REF_HRV_PV", "cm",      SignalRole.Other),
        ("DRIVDAL_INNTAK_MAGASIN_VOLUM_PV",         "Cluster1.DRIVDAL_INNTAK_MAGASIN_VOLUM_PV",         "Mill.m3", SignalRole.Other),
        ("DRIVDAL_INNTAK_MAGASIN_FYLLGRAD_PV",      "Cluster1.DRIVDAL_INNTAK_MAGASIN_FYLLGRAD_PV",      "%",       SignalRole.ReservoirFillFactor),
        ("DRIVDAL_INNTAK_MAGASIN_NED_KAP_PV",       "Cluster1.DRIVDAL_INNTAK_MAGASIN_NED_KAP_PV",       "mm",      SignalRole.LowestRegulatedLevel),
        ("DRIVDAL_INNTAK_MAGASIN_TOT_VF_PV",        "Cluster1.DRIVDAL_INNTAK_MAGASIN_TOT_VF_PV",        "m3/s",    SignalRole.Other),
        ("DRIVDAL_INNTAK_MINVF_LITER_PV",           "Cluster1.DRIVDAL_INNTAK_MINVF_LITER_PV",           "l/s",     SignalRole.Other),
        ("DRIVDAL_INNTAK_MINVF_CM_PV",              "Cluster1.DRIVDAL_INNTAK_MINVF_CM_PV",              "cm",      SignalRole.Other),
        ("DRIVDAL_INNTAK_NIVA_OVERLOP_VF_PV",       "Cluster1.DRIVDAL_INNTAK_NIVA_OVERLOP_VF_PV",       "m3/s",    SignalRole.OverflowFlow),

        ("DRIVDAL_G1_KONTROLL_AGC_DB_SP",           "Cluster1.DRIVDAL_G1_KONTROLL_AGC_DB_SP",           "None",    SignalRole.Other),
        ("DRIVDAL_KRST_KONTROLL_KOM_AL",            "Cluster1.DRIVDAL_KRST_KONTROLL_KOM_AL",            "None",    SignalRole.CommunicationAlarm),

        // NETT-side (lagt til 2026-05-18-eksport): fase L3-L-N spenning på avgangsside.
        ("DRIVDAL_NETT_LINJE_FASE_L3_U_L3_N_PV",    "Cluster1.DRIVDAL_NETT_LINJE_FASE_L3_U_L3_N_PV",    "kV",      SignalRole.ElectricalMeasurement),
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("DrivdalSignalMapSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        List<string> existing;
        try
        {
            existing = await db.SignalMaps
                .Where(x => x.PlantId == PlantId)
                .Select(x => x.SignalId)
                .ToListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            logger.LogWarning(
                "Skipped Drivdal signal-map seed: 'core.signal_map' finnes ikke ennå. " +
                "Restart etter at SCADA-skjemaet er på plass.");
            return;
        }

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
            logger.LogInformation("Seeded {Count} signal_map entries for Drivdal.", added);
        }
        else
        {
            logger.LogDebug("Drivdal signal-map already present, skipping seed.");
        }

        await UpgradeRolesAsync(db, logger, ct).ConfigureAwait(false);
    }

    // Idempotent rolle-oppgraderinger for eksisterende DB-er som ble seedet
    // før Mappings-tabellen fikk nye roller. Kan fjernes når EF-migrasjoner
    // tar over rolle-styringen.
    private static async Task UpgradeRolesAsync(KraftverkDbContext db, ILogger logger, CancellationToken ct)
    {
        var overflow = await db.SignalMaps
            .FirstOrDefaultAsync(x => x.PlantId == PlantId
                && x.SignalId == "DRIVDAL_INNTAK_NIVA_OVERLOP_VF_PV", ct)
            .ConfigureAwait(false);
        if (overflow is not null && overflow.Role != SignalRole.OverflowFlow)
        {
            overflow.Role = SignalRole.OverflowFlow;
            overflow.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "Drivdal signal-map: oppgraderte rollen for {SignalId} til OverflowFlow.",
                overflow.SignalId);
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

    /// <summary>
    /// Roller som er knyttet til en spesifikk dam (kaskade-modell). Generator-
    /// og turbin-roller forblir null fordi de er per-anlegg, ikke per-dam.
    /// </summary>
    private static bool IsDamRelated(SignalRole role) => role switch
    {
        SignalRole.OverflowFlow => true,
        SignalRole.UpstreamLevel => true,
        SignalRole.DownstreamLevel => true,
        SignalRole.ReservoirFillFactor => true,
        SignalRole.LowestRegulatedLevel => true,
        // Nye kaskade-roller (Spec KASKADE-DAMMER):
        SignalRole.GateFlow => true,
        SignalRole.GatePosition => true,
        SignalRole.TotalDamFlow => true,
        SignalRole.ReservoirVolume => true,
        _ => false,
    };
}
