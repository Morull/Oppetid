using System.Text.RegularExpressions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Seeder for Honnefoss' 113-tags whitelist (export-113-tags-avg-hour-...csv).
///
/// Topologi (per PlantTopologySeeder + SCADA-skjema 2026-05-05):
///   [Liavatn ‖ Spjodevatn] → Kydlandsvatn → Inntak (terminal) → G1
///
/// SCADA-eksporten har ALSO tags for HONNE_REVSVT_* og HONNE_NODLANDVT_* —
/// dette er øvre-magasiner i samme vassdrag som tilhører Liavatn-anlegget
/// fysisk (de seedes som dammer på <c>plant_id='liavatn'</c>).
/// For Honnefoss-anleggets kaskade-modell mapper vi dem med <c>dam_id=null</c>:
/// data lagres for traceability/historikk, men de teller ikke i Vakt-ROI for
/// Honnefoss (overflow på fjerne-magasiner regnes ikke som Honnefoss-vaktens
/// ansvar).
///
/// MapTag er pure-funksjon (testbar uten DB) og bruker prefiks-baserte regler
/// for å redusere repetisjon — 113 tags i én switch-tabell ville vært tungt.
/// </summary>
public static class HonnefossSignalMapSeeder
{
    private const string PlantId = "honnefoss";
    private const string OwnerOrgId = "dev-org";

    /// <summary>
    /// Mapper en SCADA-tag til (rolle, damId). Dam-ID-er kommer fra
    /// PlantTopologySeeder.honnefoss-topologien.
    /// </summary>
    public static (SignalRole Role, string? DamId) MapTag(string tagId)
    {
        // Generator G1 (single-generator, dam_id=null)
        if (Regex.IsMatch(tagId, @"^HONNE_G1_", RegexOptions.IgnoreCase))
        {
            return tagId switch
            {
                "HONNE_G1_GEN_P_PV"                  => (SignalRole.GeneratorActivePower, null),
                "HONNE_G1_GEN_TURTALL_PV"            => (SignalRole.GeneratorRpm, null),
                "HONNE_G1_GEN_F_PV"                  => (SignalRole.GeneratorFrequency, null),
                "HONNE_G1_GEN_COSPHI_PV"             => (SignalRole.ElectricalMeasurement, null),
                "HONNE_G1_GEN_Q_PV"                  => (SignalRole.ElectricalMeasurement, null),
                "HONNE_G1_GEN_S_PV"                  => (SignalRole.ElectricalMeasurement, null),
                "HONNE_G1_TURB_VF_PV"                => (SignalRole.TurbineWaterFlow, null),
                "HONNE_G1_TURB_VIRKNGRD_PV"          => (SignalRole.TurbineEfficiency, null),
                "HONNE_G1_TURB_LEDEAPP_POS_PV"       => (SignalRole.GuideVanePosition, null),
                "HONNE_G1_TURB_VANN_TRYKK_PV"        => (SignalRole.HydraulicPressure, null),
                "HONNE_G1_RORGATE_VANN_TRYKK_PV"     => (SignalRole.HydraulicPressure, null),
                _ => (SignalRole.Other, null), // lager-temp / regulator-tags
            };
        }

        // KRST: kontroll-/kommunikasjons-stasjon (dam_id=null)
        if (tagId.StartsWith("HONNE_KRST_", StringComparison.OrdinalIgnoreCase))
        {
            if (tagId.Contains("_KOM_AL", StringComparison.OrdinalIgnoreCase))
                return (SignalRole.CommunicationAlarm, null);
            return (SignalRole.Other, null);
        }

        // NETT: elektriske målinger på avgangsside
        if (tagId.StartsWith("HONNE_NETT_", StringComparison.OrdinalIgnoreCase))
        {
            return (SignalRole.ElectricalMeasurement, null);
        }

        // INNTAK: terminal-dam (honnefoss_inntak)
        if (tagId.StartsWith("HONNE_INNTAK_", StringComparison.OrdinalIgnoreCase))
        {
            return MapDamTag(tagId, "honnefoss_inntak");
        }

        // KYDLNDVT: Kydlandsvatn (cascade pos 2)
        if (tagId.StartsWith("HONNE_KYDLNDVT_", StringComparison.OrdinalIgnoreCase))
        {
            return MapDamTag(tagId, "honnefoss_kydlandsvatn");
        }

        // LIAVT: Liavatn-magasin i Honnefoss (cascade pos 1)
        if (tagId.StartsWith("HONNE_LIAVT_", StringComparison.OrdinalIgnoreCase))
        {
            return MapDamTag(tagId, "honnefoss_liavatn");
        }

        // SPJODEVT: Spjodevatn-magasin (cascade pos 1)
        if (tagId.StartsWith("HONNE_SPJODEVT_", StringComparison.OrdinalIgnoreCase))
        {
            return MapDamTag(tagId, "honnefoss_spjodevatn");
        }

        // REVSVT / NODLANDVT: øvre-magasiner i samme vassdrag, fysisk i Liavatn-
        // anlegget. dam_id=null for Honnefoss-mapping — data lagres uten kaskade-
        // tilknytning. Liavatn-anlegget har sine egne kopier av disse dammene.
        if (tagId.StartsWith("HONNE_REVSVT_", StringComparison.OrdinalIgnoreCase)
            || tagId.StartsWith("HONNE_NODLANDVT_", StringComparison.OrdinalIgnoreCase))
        {
            return MapDamTag(tagId, null); // null = ikke koblet til en av Honnefoss' dammer
        }

        return (SignalRole.Other, null);
    }

    /// <summary>
    /// Mapper en dam-tag til rolle basert på suffix. Brukes likt for alle
    /// dam-prefikser (INNTAK, KYDLNDVT, LIAVT, SPJODEVT, REVSVT, NODLANDVT).
    /// </summary>
    private static (SignalRole Role, string? DamId) MapDamTag(string tagId, string? damId)
    {
        if (tagId.Contains("_KOM_AL", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.CommunicationAlarm, damId);
        if (tagId.Contains("_MAG_FYLLGRD", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.ReservoirFillFactor, damId);
        if (tagId.Contains("_MAG_NEDBKAP", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.LowestRegulatedLevel, damId);
        if (tagId.Contains("_MAG_OVLOP", StringComparison.OrdinalIgnoreCase)
            || tagId.Contains("_NIVA_OVERLOP", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.OverflowFlow, damId);
        if (tagId.Contains("_MAG_VOLUM", StringComparison.OrdinalIgnoreCase)
            || tagId.Contains("_MAGASIN_VOLUM", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.ReservoirVolume, damId);
        if (tagId.Contains("_TOT_VF", StringComparison.OrdinalIgnoreCase)
            || tagId.Contains("_LUKER_TOT_VF", StringComparison.OrdinalIgnoreCase)
            || tagId.Contains("_MAGASIN_TOT_VF", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.TotalDamFlow, damId);
        if (tagId.Contains("_NIVA_SENSOR_PRI_HRV", StringComparison.OrdinalIgnoreCase)
            || tagId.Contains("_NIVA_SENSOR_PRI_KOTE", StringComparison.OrdinalIgnoreCase)
            || tagId.Contains("_NIVA_OPPSTROM_KOTE", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.UpstreamLevel, damId);
        if (tagId.Contains("_LUKE", StringComparison.OrdinalIgnoreCase) && tagId.Contains("_VF_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.GateFlow, damId);
        if (tagId.Contains("_LUKE", StringComparison.OrdinalIgnoreCase) && tagId.Contains("_POS_PV", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.GatePosition, damId);
        // _MAGASIN_FYLLGRAD og lignende fra Inntak-prefikset
        if (tagId.Contains("_MAGASIN_FYLLGRAD", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.ReservoirFillFactor, damId);
        if (tagId.Contains("_MAGASIN_NED_KAP", StringComparison.OrdinalIgnoreCase))
            return (SignalRole.LowestRegulatedLevel, damId);

        return (SignalRole.Other, damId);
    }

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("HonnefossSignalMapSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            // Hent tag-listen fra sample_facts (alle observerte tags for honnefoss).
            // Dette gjør seederen self-bootstrapping — den mapper det som faktisk
            // er importert, uten å kreve en hardkodet 113-tags-katalog.
            var observedTags = await db.SampleFacts
                .Where(s => s.AssetId == PlantId)
                .Select(s => s.SignalId)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);

            var existing = await db.SignalMaps
                .Where(x => x.PlantId == PlantId)
                .Select(x => x.SignalId)
                .ToListAsync(ct).ConfigureAwait(false);
            var existingSet = existing.ToHashSet(StringComparer.Ordinal);

            var added = 0;
            foreach (var tagId in observedTags)
            {
                if (existingSet.Contains(tagId)) continue;

                var (role, damId) = MapTag(tagId);
                db.SignalMaps.Add(new SignalMapEntry
                {
                    PlantId = PlantId,
                    SignalId = tagId,
                    CsvColumn = $"Cluster1.{tagId}",
                    Unit = "None",
                    Role = role,
                    StoreSamples = true,
                    IsActive = true,
                    OwnerOrgId = OwnerOrgId,
                    DamId = damId,
                });
                added++;
            }

            if (added > 0)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                logger.LogInformation("Honnefoss: seedet {Count} signal_map-rader.", added);
            }
            else
            {
                logger.LogDebug("Honnefoss signal-map already present, skipping seed.");
            }
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            logger.LogWarning(
                "Honnefoss-signal-seed: skjema mangler. Restart etter at SCADA-skjemaet er på plass.");
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
