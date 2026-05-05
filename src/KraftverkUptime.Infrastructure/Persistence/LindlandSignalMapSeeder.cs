using System.Text.RegularExpressions;
using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Seeder for Lindlands 117-tags whitelist (eksport-117-tags-avg-hour-...csv).
///
/// Topology (verifisert mot SCADA-eksport 2026-05-03):
/// <code>
/// HEIGRAVATN → EIAVATN → BARSTADVATN (uregulert) → ROSSLANDSHØLEN (terminal) → G1 + G2
/// </code>
///
/// SCADA-prefiks-mapping:
///   HEIGRAVT → lindland_heigravatn   (pos 1, regulert)
///   EIAVT    → lindland_eiavatn      (pos 2, regulert)
///   BARSTDVT → lindland_barstadvatn  (pos 3, uregulert — bare 2 sensorer)
///   INNTAK   → lindland_rosslandshølen (pos 4, terminal)
///   KRST     → null (sensor-stasjon på utløp etter turbinene)
///   G1, G2   → null (generator-tags; multi-generator-modell deferes til v2)
///   NETT     → null (elektriske målinger)
///
/// Phase 1 (denne seederen): dam-struktur + tag-mapping. G1/G2 mappes som
/// generator-tags med damId=null — singel-generator-aggregering brukes i v1.
/// Phase 2 (senere spec): Generator-entity + multi-generator-KPI-aggregering
/// for å skille G1/G2 i UI og effektivitets-beregning.
///
/// Idempotent: Sjekker eksisterende dammer + signal_map før insert.
/// </summary>
public static class LindlandSignalMapSeeder
{
    private const string PlantId = "lindland";
    private const string OwnerOrgId = "dev-org";

    /// <summary>
    /// 4-dam-kaskaden for Lindland. <c>IsTurbineIntake = true</c> kun på
    /// Rosslandshølen (siste før G1+G2-turbinene).
    /// </summary>
    private static readonly Dam[] Dams =
    [
        new(PlantId, "lindland_heigravatn",      "Heigravatn",       CascadePosition: 1, IsTurbineIntake: false, null, null, null),
        new(PlantId, "lindland_eiavatn",         "Eiavatn",          CascadePosition: 2, IsTurbineIntake: false, null, null, null),
        new(PlantId, "lindland_barstadvatn",     "Barstadvatn",      CascadePosition: 3, IsTurbineIntake: false, null, null, null),
        new(PlantId, "lindland_rosslandshølen",  "Rosslandshølen",   CascadePosition: 4, IsTurbineIntake: true,  null, null, null),
    ];

    /// <summary>
    /// Mapper en SCADA-tag til (rolle, damId). Generator-tags returnerer
    /// damId=null for v1; tagen lagres uansett med korrekt rolle slik at
    /// klassifikator og effektivitets-beregner kan slå opp.
    /// </summary>
    public static (SignalRole Role, string? DamId) MapTag(string tagId)
    {
        // 1) Generator-tags (G1, G2): rolle bestemmes av suffix
        var genMatch = Regex.Match(tagId, @"^LINDLAND_(G[12])_", RegexOptions.IgnoreCase);
        if (genMatch.Success)
        {
            return (MapGeneratorTagRole(tagId), null);
        }

        // 2) NETT-tags: elektriske målinger på avgangsside
        if (tagId.StartsWith("LINDLAND_NETT_", StringComparison.OrdinalIgnoreCase))
        {
            if (tagId.Contains("_KOM_AL", StringComparison.OrdinalIgnoreCase))
                return (SignalRole.CommunicationAlarm, null);
            return (SignalRole.ElectricalMeasurement, null);
        }

        // 3) KRST: sensor-stasjon (utløp), ikke dam
        if (tagId.StartsWith("LINDLAND_KRST_", StringComparison.OrdinalIgnoreCase))
        {
            if (tagId.Contains("_KOM_AL", StringComparison.OrdinalIgnoreCase))
                return (SignalRole.CommunicationAlarm, null);
            return (SignalRole.Other, null);
        }

        // 4) Dam-prefiks-mapping
        var damMatch = Regex.Match(tagId, @"^LINDLAND_(HEIGRAVT|EIAVT|BARSTDVT|INNTAK)_", RegexOptions.IgnoreCase);
        if (!damMatch.Success)
        {
            return (SignalRole.Other, null);
        }

        var damId = damMatch.Groups[1].Value.ToUpperInvariant() switch
        {
            "HEIGRAVT" => "lindland_heigravatn",
            "EIAVT"    => "lindland_eiavatn",
            "BARSTDVT" => "lindland_barstadvatn",
            "INNTAK"   => "lindland_rosslandshølen",
            _ => null,
        };
        return (MapDamTagRole(tagId), damId);
    }

    private static SignalRole MapGeneratorTagRole(string tagId)
    {
        // Suffix-baserte regler for G1/G2-tags
        if (tagId.EndsWith("_GEN_P_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.GeneratorActivePower;
        if (tagId.EndsWith("_GEN_TURTALL_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.GeneratorRpm;
        if (tagId.EndsWith("_GEN_F_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.GeneratorFrequency;
        if (tagId.EndsWith("_GEN_COSPHI_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.ElectricalMeasurement;
        if (tagId.EndsWith("_GEN_Q_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.ElectricalMeasurement;
        if (tagId.EndsWith("_GEN_S_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.ElectricalMeasurement;
        if (tagId.EndsWith("_TURB_VF_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.TurbineWaterFlow;
        if (tagId.EndsWith("_TURB_VIRKNGRD_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.TurbineEfficiency;
        if (tagId.EndsWith("_TURB_LEDEAPP_POS_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.GuideVanePosition;
        if (tagId.EndsWith("_TURB_PADRAG_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.TurbinePadrag;
        if (tagId.EndsWith("_TURB_VANN_TRYKK_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.HydraulicPressure;
        if (tagId.EndsWith("_RORGATE_VANN_TRYKK_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.HydraulicPressure;
        if (tagId.EndsWith("_HYDRL_OLJE_TRYKK_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.HydraulicPressure;
        if (tagId.Contains("_KOM_AL", StringComparison.OrdinalIgnoreCase)) return SignalRole.CommunicationAlarm;
        if (tagId.Contains("_TEMP_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.ConditionTemperature;
        // Settpunkt-tags + andre styre-signaler
        return SignalRole.Other;
    }

    private static SignalRole MapDamTagRole(string tagId)
    {
        // Overflow-vannføring (kontinuerlig flow) — kritisk for Vakt-ROI
        if (tagId.EndsWith("_NIVA_OVERLOP_VF_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.OverflowFlow;
        if (tagId.EndsWith("_KONTROLL_MAG_OVLOP_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.OverflowFlow;

        // Magasin-fyllgrad / volum
        if (tagId.EndsWith("_KONTROLL_MAG_FYLLGRD_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.ReservoirFillFactor;
        if (tagId.EndsWith("_MAGASIN_FYLLGRAD_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.ReservoirFillFactor;
        if (tagId.EndsWith("_KONTROLL_MAG_VOLUM_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.ReservoirVolume;
        if (tagId.EndsWith("_MAGASIN_VOLUM_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.ReservoirVolume;

        // Vannføring (luker, total)
        if (tagId.EndsWith("_LUKE1_VF_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.GateFlow;
        if (tagId.EndsWith("_LUKE1_POS_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.GatePosition;
        if (tagId.EndsWith("_KONTROLL_TOT_VF_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.TotalDamFlow;

        // Nivåer
        if (tagId.EndsWith("_NIVA_OPPSTROM_KOTE_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.UpstreamLevel;
        if (tagId.EndsWith("_NIVA_SENSOR_PRI_KOTE_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.UpstreamLevel;
        if (tagId.EndsWith("_NIVA_NEDSTROM_KOTE_PV", StringComparison.OrdinalIgnoreCase)) return SignalRole.DownstreamLevel;

        // Kommunikasjons-alarm per dam
        if (tagId.Contains("_KOM_AL", StringComparison.OrdinalIgnoreCase)) return SignalRole.CommunicationAlarm;

        // Resterende: minstevannføring, met-data, settpunkter — Other
        return SignalRole.Other;
    }

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("LindlandSignalMapSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            await SeedDamsAsync(db, logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            logger.LogWarning(
                "Lindland-seed: 'core.dams' eller 'core.signal_map' finnes ikke ennå. " +
                "Restart etter at SCADA-skjemaet er på plass.");
        }
    }

    private static async Task SeedDamsAsync(KraftverkDbContext db, ILogger logger, CancellationToken ct)
    {
        // 1) Sikre at de 4 dammene finnes. Hvis 'lindland_main' (default-dam fra
        // DefaultDamSeeder) finnes, fjern den når vi har erstattet den med
        // den ekte kaskaden. Idempotent.
        var existingDams = await db.Dams
            .Where(d => d.PlantId == PlantId)
            .ToListAsync(ct).ConfigureAwait(false);
        var existingDamIds = existingDams.Select(d => d.DamId).ToHashSet(StringComparer.Ordinal);

        var damsAdded = 0;
        foreach (var dam in Dams)
        {
            if (existingDamIds.Contains(dam.DamId)) continue;
            // Hvis vi setter en NY terminal-dam, fjern terminal-flagget fra
            // alle eksisterende dammer (deferrable constraint i Postgres)
            if (dam.IsTurbineIntake)
            {
                foreach (var prev in existingDams.Where(d => d.IsTurbineIntake))
                {
                    prev.IsTurbineIntake = false;
                }
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
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
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            damsAdded++;
        }
        if (damsAdded > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Lindland: seedet {Count} dammer.", damsAdded);
        }

        // 2) Hvis default-'lindland_main' fortsatt eksisterer og vi har
        //    rosslandshølen som ny terminal: rydd opp default-en.
        var legacyMain = existingDams.FirstOrDefault(d => d.DamId == "lindland_main");
        if (legacyMain is not null
            && existingDamIds.Add(Dams[3].DamId)) // dummy check at ny terminal er nylig opprettet
        {
            // Flytt eventuelle signal_map-rader fra default-dam til Rosslandshølen
            await db.SignalMaps
                .Where(s => s.PlantId == PlantId && s.DamId == "lindland_main")
                .ExecuteUpdateAsync(setters =>
                    setters.SetProperty(s => s.DamId, "lindland_rosslandshølen"), ct)
                .ConfigureAwait(false);

            db.Dams.Remove(legacyMain);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Lindland: fjernet legacy 'lindland_main' default-dam.");
        }

        // 3) Seed signal_map. Tag-listen hentes fra SCADA-eksport-headeren
        // dynamisk; her hardkodes 117-tags fra 2026-05-03-eksporten som
        // best-effort-bootstrap. Fremtidige eksporter med nye tags vil bli
        // upserted via SCADA-import (som ikke krever pre-seeding).
        var existingSignals = await db.SignalMaps
            .Where(s => s.PlantId == PlantId)
            .Select(s => s.SignalId)
            .ToListAsync(ct).ConfigureAwait(false);
        var existingSet = existingSignals.ToHashSet(StringComparer.Ordinal);

        var signalsAdded = 0;
        foreach (var tag in Lindland117TagCatalog.Tags)
        {
            if (existingSet.Contains(tag)) continue;
            var (role, damId) = MapTag(tag);
            db.SignalMaps.Add(new SignalMapEntry
            {
                PlantId = PlantId,
                SignalId = tag,
                CsvColumn = $"Value (Cluster1.{tag})",
                Unit = "",  // populeres ved første import
                Role = role,
                StoreSamples = true,
                IsActive = true,
                DamId = damId,
                OwnerOrgId = OwnerOrgId,
            });
            signalsAdded++;
        }
        if (signalsAdded > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Lindland: seedet {Count} signal_map-rader.", signalsAdded);
        }
        else
        {
            logger.LogDebug("Lindland signal-map already present, skipping seed.");
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
