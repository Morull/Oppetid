using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Seeder for Hauklands 195-tags whitelist (eksport-195-tags-avg-hour-...csv).
/// Auto-mapping fra CSV-header-prefix/suffix til <see cref="SignalRole"/> +
/// <c>DamId</c>, basert på regelsett. Er testbar fordi
/// <see cref="MapTag"/> er ren static-funksjon — alle 195 tags kan
/// mappes deterministisk uten DB.
///
/// Topologi (4 dammer i kaskade):
///   - Stølsvatn (haukland_stolsvt) — øvre dam, parallel med Gjelevatn
///   - Gjelevatn (haukland_gjelevt) — øvre dam, parallel med Stølsvatn
///   - Skårstemmevatn (haukland_skrstmvt) — mellomdam, mottar fra Gjelevatn
///   - Stemmevatn (haukland_stemmevt) — TERMINAL-dam, mottar fra
///     Stølsvatn + Skårstemmevatn, mater turbin G1
///
/// Stemmevatn mangler LUKE1_VF_PV/LUKE1_POS_PV — vannet går rett til turbin
/// (bekreftet av drifts-leder).
///
/// Idempotent — hopper over tags som allerede finnes for plant 'haukland'.
/// </summary>
public static class HauklandSignalMapSeeder
{
    private const string PlantId = "haukland";
    private const string OwnerOrgId = "dev-org";

    /// <summary>De 4 dammene i Haukland-kaskaden. Stemmevatn er terminal.</summary>
    public static readonly Dam[] Dams =
    [
        new Dam(PlantId, "haukland_stolsvt",  "Stølsvatn",      CascadePosition: 1, IsTurbineIntake: false, null, null, null),
        new Dam(PlantId, "haukland_gjelevt",  "Gjelevatn",      CascadePosition: 1, IsTurbineIntake: false, null, null, null),
        new Dam(PlantId, "haukland_skrstmvt", "Skårstemmevatn", CascadePosition: 2, IsTurbineIntake: false, null, null, null),
        new Dam(PlantId, "haukland_stemmevt", "Stemmevatn",     CascadePosition: 3, IsTurbineIntake: true,  null, null, null),
    ];

    /// <summary>
    /// Mapper en tag-id til (rolle, damId, storeSamples, enhet). Pure-funksjon
    /// — testbar uten DB. <c>damId = null</c> for generator/turbin/sentral-tags.
    /// <c>storeSamples = false</c> for "støy"-tags (lager-temp, hjelpekraft osv.)
    /// som vi har whitelistet for fremtidig bruk men ikke trenger for KPI-er.
    /// </summary>
    public static (SignalRole role, string? damId, bool storeSamples, string unitDefault) MapTag(string tagId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagId);

        // ---- Generator G1 (DamId = null) -------------------------------------
        if (tagId.StartsWith("HAUKLAND_G1_", StringComparison.Ordinal))
        {
            return tagId switch
            {
                "HAUKLAND_G1_GEN_P_PV"                              => (SignalRole.GeneratorActivePower, null, true, "kW"),
                "HAUKLAND_G1_GEN_TURTALL_PV"                        => (SignalRole.GeneratorRpm, null, true, "rpm"),
                "HAUKLAND_G1_GEN_F_PV"                              => (SignalRole.GeneratorFrequency, null, true, "Hz"),
                "HAUKLAND_G1_GEN_COSPHI_PV"                         => (SignalRole.ElectricalMeasurement, null, true, "cosp"),
                "HAUKLAND_G1_GEN_Q_PV"                              => (SignalRole.ElectricalMeasurement, null, true, "kVAr"),
                "HAUKLAND_G1_TURB_VF_PV"                            => (SignalRole.TurbineWaterFlow, null, true, "m3/s"),
                "HAUKLAND_G1_TURB_VIRKNGRD_PV"                      => (SignalRole.TurbineEfficiency, null, true, "%"),
                "HAUKLAND_G1_TURB_LEDEAPP_POS_PV"                   => (SignalRole.GuideVanePosition, null, true, "%"),
                "HAUKLAND_G1_KONTROLL_TURBINREG_PADRAG_SP_PV"       => (SignalRole.TurbinePadrag, null, true, "%"),
                "HAUKLAND_G1_TURB_VANN_TRYKK_PV"                    => (SignalRole.HydraulicPressure, null, true, "mvs"),
                "HAUKLAND_G1_HYDRL_OLJE_TRYKK_PV"                   => (SignalRole.HydraulicPressure, null, true, "bar"),
                "HAUKLAND_G1_RORGATE_VANN_TRYKK_PV"                 => (SignalRole.HydraulicPressure, null, true, "mKVs"),
                "HAUKLAND_G1_KONTROLL_AGC_DB_SP"                    => (SignalRole.Other, null, false, "None"),
                _ => (SignalRole.Other, null, false, "None"), // andre G1-tags (temp/vibrasjon/elek) — whitelist-only
            };
        }

        // ---- Sentral-anlegg / nett / inntak (DamId = null eller terminal) ----
        if (tagId.StartsWith("HAUKLAND_KRST_", StringComparison.Ordinal))
        {
            return tagId switch
            {
                "HAUKLAND_KRST_KONTROLL_KOM_AL"   => (SignalRole.CommunicationAlarm, null, true, "None"),
                _                                  => (SignalRole.Other, null, false, "None"),
            };
        }
        if (tagId.StartsWith("HAUKLAND_NETT_", StringComparison.Ordinal))
        {
            return (SignalRole.ElectricalMeasurement, null, false, "kV");
        }
        if (tagId.StartsWith("HAUKLAND_VANNVEI_", StringComparison.Ordinal))
        {
            return (SignalRole.Other, null, false, "VDC");
        }
        // INNTAK = inntak til turbin = terminal-dam (Stemmevatn).
        if (tagId.StartsWith("HAUKLAND_INNTAK_", StringComparison.Ordinal))
        {
            return tagId switch
            {
                "HAUKLAND_INNTAK_NIVA_OPPSTROM_KOTE_PV" => (SignalRole.UpstreamLevel, "haukland_stemmevt", true, "moh"),
                _ => (SignalRole.Other, "haukland_stemmevt", false, "None"),
            };
        }
        // V26_-prefix er en eldre AGC-konfig-tag, hører ikke til noen dam.
        if (tagId.StartsWith("V26_", StringComparison.Ordinal))
        {
            return (SignalRole.Other, null, false, "None");
        }

        // ---- Dam-prefix-deteksjon -------------------------------------------
        var damId = ExtractDamId(tagId);
        if (damId is null)
        {
            return (SignalRole.Other, null, false, "None");
        }

        // ---- Suffix-basert rolle for dam-tags --------------------------------
        return tagId switch
        {
            // Standard dam-roller
            _ when tagId.EndsWith("_NIVA_MOH_PV", StringComparison.Ordinal)
                => (SignalRole.UpstreamLevel, damId, true, "moh"),
            _ when tagId.EndsWith("_KONTROLL_MAG_FYLLGRD_PV", StringComparison.Ordinal)
                => (SignalRole.ReservoirFillFactor, damId, true, "%"),
            _ when tagId.EndsWith("_KONTROLL_MAG_OVLOP_PV", StringComparison.Ordinal)
                => (SignalRole.OverflowFlow, damId, true, "m3/s"),
            _ when tagId.EndsWith("_KONTROLL_MAG_VOLUM_PV", StringComparison.Ordinal)
                => (SignalRole.ReservoirVolume, damId, true, "Mill.m3"),
            _ when tagId.EndsWith("_KONTROLL_MAG_NEDBKAP_PV", StringComparison.Ordinal)
                => (SignalRole.LowestRegulatedLevel, damId, true, "mm"),
            // Kaskade-spesifikke roller (Stemmevatn mangler LUKE1 — håndteres ved
            // at den ikke har slike tags i CSV-eksporten, ikke logisk her)
            _ when tagId.EndsWith("_LUKE1_VF_PV", StringComparison.Ordinal)
                => (SignalRole.GateFlow, damId, true, "m3/s"),
            _ when tagId.EndsWith("_LUKE1_POS_PV", StringComparison.Ordinal)
                => (SignalRole.GatePosition, damId, true, "cm"),
            _ when tagId.EndsWith("_KONTROLL_TOT_VF_PV", StringComparison.Ordinal)
                // SCADA-feilkonfig: enhet rapportert som "None" istedenfor m3/s.
                // Forutsett m3/s i seederen; runtime-validering er separat.
                => (SignalRole.TotalDamFlow, damId, true, "m3/s"),
            _ when tagId.EndsWith("_KONTROLL_KOM_AL", StringComparison.Ordinal)
                => (SignalRole.CommunicationAlarm, damId, true, "None"),
            // Andre dam-tags (lekkasje, met-stasjon, hjelpekraft, AGC-config):
            // beholdt med DamId for opprinnelse, men ikke aktive samples.
            _ => (SignalRole.Other, damId, false, "None"),
        };
    }

    /// <summary>
    /// Trekker ut dam-id fra tag-prefix. Returnerer null hvis tagen ikke
    /// hører til noen dam.
    /// </summary>
    public static string? ExtractDamId(string tagId)
    {
        if (tagId.StartsWith("HAUKLAND_STOLSVT_", StringComparison.Ordinal))  return "haukland_stolsvt";
        if (tagId.StartsWith("HAUKLAND_GJELEVT_", StringComparison.Ordinal))  return "haukland_gjelevt";
        if (tagId.StartsWith("HAUKLAND_SKRSTMVT_", StringComparison.Ordinal)) return "haukland_skrstmvt";
        if (tagId.StartsWith("HAUKLAND_STEMMEVT_", StringComparison.Ordinal)) return "haukland_stemmevt";
        return null;
    }

    /// <summary>
    /// Bygger CSV-kolonne-navn som matcher KraftScada-eksportens header:
    /// <c>Cluster1.{tagId}</c>. Importeren matcher mot dette.
    /// </summary>
    private static string CsvColumn(string tagId) => $"Cluster1.{tagId}";

    /// <summary>
    /// Seeder dammer + signal_map for Haukland. Idempotent: hopper over
    /// dammer/tags som allerede finnes. Forutsetter at SCADA-foundation-skjemaet
    /// er på plass (signal_map) og at dams-skjemaet er aktivert (Steg 1).
    /// </summary>
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("HauklandSignalMapSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        // 1) Seed dammene først (idempotent)
        try
        {
            var existingDamIds = await db.Dams
                .Where(d => d.PlantId == PlantId)
                .Select(d => d.DamId)
                .ToListAsync(ct).ConfigureAwait(false);
            var existingSet = existingDamIds.ToHashSet(StringComparer.Ordinal);

            // Hvis backfill har lagt inn 'haukland_main' som default, fjern den
            // før vi setter inn de fire ekte dammene — ellers brytes
            // one-intake-per-plant-constraint når vi setter Stemmevatn til terminal.
            if (existingSet.Contains($"{PlantId}_main") && !existingSet.Contains("haukland_stemmevt"))
            {
                var defaultDam = await db.Dams
                    .FirstOrDefaultAsync(d => d.PlantId == PlantId && d.DamId == $"{PlantId}_main", ct)
                    .ConfigureAwait(false);
                if (defaultDam is not null)
                {
                    db.Dams.Remove(defaultDam);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    existingSet.Remove($"{PlantId}_main");
                    logger.LogInformation(
                        "Haukland: fjernet backfill-default-dam '{PlantId}_main' før innsetting av kaskade-dammer.",
                        PlantId);
                }
            }

            var damsAdded = 0;
            foreach (var dam in Dams)
            {
                if (existingSet.Contains(dam.DamId)) continue;
                db.Dams.Add(new DamEntry
                {
                    PlantId = dam.PlantId,
                    DamId = dam.DamId,
                    Name = dam.Name,
                    CascadePosition = dam.CascadePosition,
                    IsTurbineIntake = dam.IsTurbineIntake,
                    HrvMoh = dam.HrvMoh,
                    LrvMoh = dam.LrvMoh,
                    VolumeMm3 = dam.VolumeMm3,
                    OwnerOrgId = OwnerOrgId,
                });
                damsAdded++;
            }
            if (damsAdded > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                logger.LogInformation("Haukland: seedet {Count} dammer.", damsAdded);
            }
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            logger.LogWarning(
                "Haukland-seed: 'core.dams' eller 'core.signal_map' finnes ikke ennå. " +
                "Restart etter at skjemaet er på plass.");
            return;
        }

        // 2) Seed signal_map fra hardkodet tag-liste. Auto-mapping kjøres
        //    via MapTag på 195 tags; resultatet er deterministisk.
        try
        {
            var existingSignalIds = await db.SignalMaps
                .Where(x => x.PlantId == PlantId)
                .Select(x => x.SignalId)
                .ToListAsync(ct).ConfigureAwait(false);
            var existingSet = existingSignalIds.ToHashSet(StringComparer.Ordinal);

            var added = 0;
            foreach (var tagId in HauklandTagCatalog.AllTags)
            {
                if (existingSet.Contains(tagId)) continue;

                var (role, damId, storeSamples, unit) = MapTag(tagId);
                db.SignalMaps.Add(new SignalMapEntry
                {
                    PlantId = PlantId,
                    SignalId = tagId,
                    CsvColumn = CsvColumn(tagId),
                    Unit = unit,
                    Role = role,
                    StoreSamples = storeSamples,
                    IsActive = true,
                    OwnerOrgId = OwnerOrgId,
                    DamId = damId,
                });
                added++;
            }

            if (added > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                logger.LogInformation("Haukland: seedet {Count} signal_map-rader.", added);
            }
            else
            {
                logger.LogDebug("Haukland signal-map already present, skipping seed.");
            }
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            logger.LogWarning(
                "Haukland-signal-seed: 'core.signal_map' finnes ikke ennå. " +
                "Restart etter at SCADA-skjemaet er på plass.");
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
