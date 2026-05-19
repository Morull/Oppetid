using System.Text.RegularExpressions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Seeder for resten av Dalane Kraft-porteføljen: Vikeså, Stølskraft,
/// Ørsdalen, Øgreyfoss, Løgjen. Mapper 72 SCADA-tags fra master-eksporten
/// 2026-05-18 (export-73-tags-avg-hour-20260518-160921_MASTER.csv) — den
/// 73. tagen (DRIVDAL_NETT_...) ligger i <see cref="DrivdalSignalMapSeeder"/>.
///
/// Topologi: alle 5 anleggene har én default terminal-dam (<c>{plantId}_main</c>)
/// som <see cref="DefaultDamSeeder"/> oppretter. INNTAK-tags festes til
/// denne dammen; generator/KRST/NETT-tags har <c>damId = null</c>.
///
/// Øgreyfoss er spesiell: SCADA-eksporten har to prefikser — <c>OGREY1_</c>
/// (G1-side, inneholder INNTAK/magasin) og <c>OGREY2_</c> (G2-side, kun
/// generator-tags). Begge mapper til samme <c>plant_id = "ogreyfoss"</c>.
/// INNTAK-tags festes til <c>ogreyfoss_ogreyvatn</c> som er terminal-dam i
/// 7-dam-kaskaden (se <see cref="PlantTopologySeeder"/>) — ikke til en
/// hypotetisk <c>ogreyfoss_main</c> som PlantTopologySeeder ville slettet.
///
/// Idempotent — hopper over tags som allerede finnes i signal_map for
/// gjeldende plant_id. Fremtidige eksporter med nye tags blir upserted via
/// SCADA-importen, ikke via denne seederen.
/// </summary>
public static class DalanePortfolioSignalMapSeeder
{
    private const string OwnerOrgId = "dev-org";

    /// <summary>
    /// SCADA-prefiks → plant_id. To-prefiks-mapping (OGREY1+OGREY2) håndteres
    /// ved at begge peker på samme plant_id; det blir 6 oppslag for 5 anlegg.
    /// </summary>
    private static readonly (string Prefix, string PlantId)[] PrefixToPlant =
    [
        ("VIKESA_",     "vikesa"),
        ("STOLSKRAFT_", "stolskraft"),
        ("ORSDAL_",     "orsdalen"),
        ("OGREY1_",     "ogreyfoss"),
        ("OGREY2_",     "ogreyfoss"),
        ("LOGJEN_",     "logjen"),
    ];

    /// <summary>
    /// Mapper en SCADA-tag til (plant_id, rolle, damId, default-enhet).
    /// Pure-funksjon — testbar uten DB. PlantId er tom streng for ukjente
    /// prefikser; <see cref="SeedAsync"/> hopper i så fall over tagen.
    /// </summary>
    public static (string PlantId, SignalRole Role, string? DamId, string UnitDefault) MapTag(string tagId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagId);

        var match = PrefixToPlant.FirstOrDefault(p =>
            tagId.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase));
        if (match.PlantId is null)
        {
            return ("", SignalRole.Other, null, "None");
        }

        var plantId = match.PlantId;
        // Default: SingleDam-anlegg har terminal-dam '{plant}_main'. MultiDam-
        // anlegg (Øgreyfoss = 7-dam-kaskade) bruker terminal-dam som
        // PlantTopologySeeder oppretter. Listen MÅ holdes synkronisert med
        // PlantTopologySeeder.Topologies — ellers blir INNTAK-tags foreldreløst.
        var damId = plantId switch
        {
            "ogreyfoss" => "ogreyfoss_ogreyvatn",
            _ => $"{plantId}_main",
        };
        var withoutPrefix = tagId[match.Prefix.Length..];

        // KRST = sensor-stasjon på utløp, ikke dam
        if (withoutPrefix.StartsWith("KRST_", StringComparison.OrdinalIgnoreCase))
        {
            if (tagId.Contains("_KOM_AL", StringComparison.OrdinalIgnoreCase))
                return (plantId, SignalRole.CommunicationAlarm, null, "None");
            return (plantId, SignalRole.Other, null, "None");
        }

        // NETT = elektriske målinger på avgangsside (per-anlegg, ikke per-dam)
        if (withoutPrefix.StartsWith("NETT_", StringComparison.OrdinalIgnoreCase))
        {
            return (plantId, SignalRole.ElectricalMeasurement, null, "kV");
        }

        // Generator-side: G1 eller G2 (Øgreyfoss har begge)
        if (Regex.IsMatch(withoutPrefix, @"^G[12]_", RegexOptions.IgnoreCase))
        {
            var (genRole, genUnit) = MapGeneratorRoleAndUnit(tagId);
            return (plantId, genRole, null, genUnit);
        }

        // INNTAK → terminal-dam ('{plant}_main')
        if (withoutPrefix.StartsWith("INNTAK_", StringComparison.OrdinalIgnoreCase))
        {
            var (inntakRole, inntakUnit) = MapInntakRoleAndUnit(tagId);
            return (plantId, inntakRole, IsDamRelated(inntakRole) ? damId : null, inntakUnit);
        }

        return (plantId, SignalRole.Other, null, "None");
    }

    private static (SignalRole Role, string Unit) MapGeneratorRoleAndUnit(string tagId)
    {
        // Generator-tags
        if (tagId.EndsWith("_GEN_P_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.GeneratorActivePower, "kW");
        if (tagId.EndsWith("_GEN_TURTALL_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.GeneratorRpm, "rpm");
        // Løgjen-variant: SCADA bruker _PRST_PV-suffix istedenfor _PV
        if (tagId.EndsWith("_GEN_TURTALL_PRST_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.GeneratorRpm, "rpm");
        if (tagId.EndsWith("_GEN_F_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.GeneratorFrequency, "Hz");
        if (tagId.EndsWith("_GEN_COSPHI_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.ElectricalMeasurement, "cosp");
        if (tagId.EndsWith("_GEN_Q_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.ElectricalMeasurement, "kVAr");
        if (tagId.EndsWith("_GEN_S_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.ElectricalMeasurement, "kVA");

        // Turbin-tags
        if (tagId.EndsWith("_TURB_VF_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.TurbineWaterFlow, "m3/s");
        if (tagId.EndsWith("_TURB_VIRKNGRD_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.TurbineEfficiency, "%");
        if (tagId.EndsWith("_TURB_LEDEAPP_POS_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.GuideVanePosition, "%");
        if (tagId.EndsWith("_TURB_PADRAG_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.TurbinePadrag, "%");
        if (tagId.EndsWith("_TURB_VANN_TRYKK_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.HydraulicPressure, "mVs");

        // Hydraulikk / rørgate
        if (tagId.EndsWith("_RORGATE_VANN_TRYKK_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.HydraulicPressure, "bar");
        if (tagId.EndsWith("_HYDRL_OLJE_TRYKK_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.HydraulicPressure, "bar");

        // Diverse alarmer + temperaturer
        if (tagId.Contains("_KOM_AL", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.CommunicationAlarm, "None");
        if (tagId.Contains("_TEMP_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.ConditionTemperature, "°C");

        // Settpunkt-tags + andre styre-signaler (REG_*_SP_*) er ikke analyse-relevante
        return (SignalRole.Other, "None");
    }

    private static (SignalRole Role, string Unit) MapInntakRoleAndUnit(string tagId)
    {
        // Overflow (kritisk for Vakt-ROI)
        if (tagId.EndsWith("_NIVA_OVERLOP_VF_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.OverflowFlow, "m3/s");

        // Magasin
        if (tagId.EndsWith("_MAGASIN_FYLLGRAD_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.ReservoirFillFactor, "%");
        if (tagId.EndsWith("_MAGASIN_VOLUM_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.ReservoirVolume, "Mill.m3");
        if (tagId.EndsWith("_MAGASIN_TOT_VF_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.TotalDamFlow, "m3/s");
        if (tagId.EndsWith("_MAGASIN_NED_KAP_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.LowestRegulatedLevel, "mm");

        // Nivåer
        if (tagId.EndsWith("_NIVA_OPPSTROM_KOTE_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.UpstreamLevel, "moh");
        if (tagId.EndsWith("_NIVA_NEDSTROM_KOTE_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.DownstreamLevel, "moh");

        // REF_HRV-varianter er offset-målinger (cm fra HRV) — beholdes som Other
        // for å unngå dobbeltbruk i nivå-baserte beregninger
        if (tagId.EndsWith("_NIVA_OPPSTROM_REF_HRV_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.Other, "cm");
        if (tagId.EndsWith("_NIVA_NEDSTROM_REF_HRV_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.Other, "cm");

        // Verdi-til-aksjon (VTA) — sekundær level-reading, ikke analyse-kritisk
        if (tagId.EndsWith("_NIVA_VTA_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.Other, "moh");

        // Minstevannføring (regulator-krav, ikke prod-relatert)
        if (tagId.EndsWith("_MINVF_LITER_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.Other, "l/s");
        if (tagId.EndsWith("_MINVF_CM_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.Other, "cm");

        return (SignalRole.Other, "None");
    }

    /// <summary>
    /// Roller som tilhører dam-entiteten (kaskade-modell). Generator/turbin-
    /// roller forblir <c>damId = null</c> fordi de er per-anlegg, ikke per-dam.
    /// </summary>
    private static bool IsDamRelated(SignalRole role) => role switch
    {
        SignalRole.OverflowFlow => true,
        SignalRole.UpstreamLevel => true,
        SignalRole.DownstreamLevel => true,
        SignalRole.ReservoirFillFactor => true,
        SignalRole.ReservoirVolume => true,
        SignalRole.LowestRegulatedLevel => true,
        SignalRole.GateFlow => true,
        SignalRole.GatePosition => true,
        SignalRole.TotalDamFlow => true,
        _ => false,
    };

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("DalanePortfolioSignalMapSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            // Hent eksisterende signal-IDs gruppert per plant for å unngå
            // dupliserte rader. Idempotent: skipper allerede-seedete tags.
            var plantIds = PrefixToPlant.Select(p => p.PlantId).Distinct().ToList();
            var existing = await db.SignalMaps
                .Where(s => plantIds.Contains(s.PlantId))
                .Select(s => new { s.PlantId, s.SignalId })
                .ToListAsync(ct).ConfigureAwait(false);
            var existingByPlant = existing
                .GroupBy(x => x.PlantId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.SignalId).ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);

            var added = 0;
            var perPlantCount = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var tag in DalanePortfolio72TagCatalog.Tags)
            {
                var (plantId, role, damId, unit) = MapTag(tag);
                if (string.IsNullOrEmpty(plantId))
                {
                    logger.LogWarning("Hopper over tag '{Tag}' — ukjent prefix.", tag);
                    continue;
                }

                if (existingByPlant.TryGetValue(plantId, out var set) && set.Contains(tag))
                    continue;

                db.SignalMaps.Add(new SignalMapEntry
                {
                    PlantId = plantId,
                    SignalId = tag,
                    CsvColumn = $"Cluster1.{tag}",
                    Unit = unit,
                    Role = role,
                    StoreSamples = true,
                    IsActive = true,
                    OwnerOrgId = OwnerOrgId,
                    DamId = damId,
                });
                added++;
                perPlantCount[plantId] = perPlantCount.GetValueOrDefault(plantId) + 1;
            }

            if (added > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                foreach (var (plant, count) in perPlantCount.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    logger.LogInformation(
                        "DalanePortfolio: seedet {Count} signal_map-rader for {PlantId}.",
                        count, plant);
                }
            }
            else
            {
                logger.LogDebug("DalanePortfolio signal-map already present, skipping seed.");
            }
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            logger.LogWarning(
                "DalanePortfolio-seed: 'core.signal_map' finnes ikke ennå. " +
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
