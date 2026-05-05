using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Seeder for dam-topologi for de 9 anleggene som ennå ikke har dedikert
/// SignalMap-seeder. Idempotent. Kjører ETTER <see cref="DefaultDamSeeder"/>
/// slik at den kan oppgradere/erstatte default-dammene som backfill opprettet.
///
/// Topologi for hver kraftverk er bekreftet mot SCADA-skjemaene under
/// <c>SCADA kraftverk skjema/</c> 2026-05-05.
///
/// To strategier:
/// <list type="number">
///   <item>
///     <b>Single-dam-anlegg</b> (drivdal, grodemfoss, logjen, orsdalen, stolskraft,
///     vikesa): Beholder <c>{plantId}_main</c> som dam-id og oppdaterer KUN navnet
///     til det riktige fra SCADA. Trygt fordi eksisterende signal_map-rader
///     fra DrivdalSignalMapSeeder peker til <c>drivdal_main</c> og fortsetter
///     å fungere.
///   </item>
///   <item>
///     <b>Multi-dam-kaskader</b> (honnefoss, liavatn, ogreyfoss): Fjerner
///     <c>{plantId}_main</c> hvis den finnes (samme mønster som
///     <see cref="HauklandSignalMapSeeder"/>) og setter inn navngitte dammer.
///     Ingen signal_map-rader er avhengig av <c>_main</c> for disse anleggene
///     (de har ikke dedikert SCADA-seeder ennå).
///   </item>
/// </list>
///
/// Anlegg utenfor scope (har egen seeder): haukland, lindland.
/// </summary>
public static class PlantTopologySeeder
{
    private const string OwnerOrgId = "dev-org";

    /// <summary>Beskriver én dam i en kraftverks-topologi.</summary>
    /// <param name="DamId">Stabil identifikator (FK fra signal_map).</param>
    /// <param name="Name">Visningsnavn slik det står i SCADA-skjemaet.</param>
    /// <param name="CascadePosition">1 = øverst i kaskaden; flere kan dele posisjon.</param>
    /// <param name="IsTurbineIntake">Eksakt én dam per anlegg er turbin-inntak.</param>
    public sealed record DamSpec(
        string DamId, string Name, int CascadePosition, bool IsTurbineIntake);

    /// <summary>Topologi-beskrivelse per anlegg.</summary>
    /// <param name="PlantId">Plant-ID som matcher core.plants.</param>
    /// <param name="Strategy">SingleDam = oppdater navn på _main; MultiDam = erstatt _main.</param>
    /// <param name="Dams">Dammene i anlegget. Eksakt én må ha IsTurbineIntake=true.</param>
    public sealed record PlantTopology(
        string PlantId, TopologyStrategy Strategy, DamSpec[] Dams);

    public enum TopologyStrategy
    {
        /// <summary>Beholder dam_id={plantId}_main, oppdaterer kun Name.</summary>
        SingleDam,
        /// <summary>Fjerner {plantId}_main, setter inn navngitte dammer.</summary>
        MultiDam,
    }

    /// <summary>
    /// Topologi for de 9 anleggene som mangler dedikert SignalMap-seeder.
    /// Verifisert mot SCADA-skjemaer 2026-05-05.
    /// </summary>
    internal static readonly PlantTopology[] Topologies =
    [
        // ---- Single-dam-anlegg ------------------------------------------------
        new("drivdal", TopologyStrategy.SingleDam,
        [
            new("drivdal_main", "Drivdalsvatn", CascadePosition: 1, IsTurbineIntake: true),
        ]),

        // Grødemfoss har historisk hatt 2 generatorer (G1, G2). G1 er havarert
        // 2026-05-05 og kommer ikke til å bli reparert iflg drifts-leder —
        // kun G2 produserer. InstalledCapacityMw=2.8 i PlantPortfolioSeeder
        // gjelder G2 alene (skal IKKE reduseres). Dam-modellen er 1 dam
        // (Smievatn); generator-status håndteres i SignalMap når det legges til.
        new("grodemfoss", TopologyStrategy.SingleDam,
        [
            new("grodemfoss_main", "Smievatn", CascadePosition: 1, IsTurbineIntake: true),
        ]),

        new("logjen", TopologyStrategy.SingleDam,
        [
            new("logjen_main", "Åvedalsvatn", CascadePosition: 1, IsTurbineIntake: true),
        ]),

        new("orsdalen", TopologyStrategy.SingleDam,
        [
            new("orsdalen_main", "Inntak", CascadePosition: 1, IsTurbineIntake: true),
        ]),

        // Stølsvatn er eid av IVAR (vannverk); produksjon er vannforbruks-styrt.
        new("stolskraft", TopologyStrategy.SingleDam,
        [
            new("stolskraft_main", "Stølsvatn (IVAR)", CascadePosition: 1, IsTurbineIntake: true),
        ]),

        new("vikesa", TopologyStrategy.SingleDam,
        [
            new("vikesa_main", "Storrsheivatn", CascadePosition: 1, IsTurbineIntake: true),
        ]),

        // ---- Multi-dam-kaskader -----------------------------------------------

        // Honnefoss-kaskaden:
        //   [Liavatn ‖ Spjodevatn] → Kydlandsvatn → Inntak (terminal) → G1
        // NB: dam-navnet "Liavatn" her er et magasin i Honnefoss-anlegget,
        // ikke det selvstendige Liavatn-kraftverket.
        new("honnefoss", TopologyStrategy.MultiDam,
        [
            new("honnefoss_liavatn",       "Liavatn",       CascadePosition: 1, IsTurbineIntake: false),
            new("honnefoss_spjodevatn",    "Spjodevatn",    CascadePosition: 1, IsTurbineIntake: false),
            new("honnefoss_kydlandsvatn",  "Kydlandsvatn",  CascadePosition: 2, IsTurbineIntake: false),
            new("honnefoss_inntak",        "Inntak",        CascadePosition: 3, IsTurbineIntake: true),
        ]),

        // Liavatn-kraftverket:
        //   Revsvatn → Nodlandsvatn → Stokkurhølen (turbin-inntak) → G1 → Liavatn (utløp)
        // "Liavatn" nederst er utløps-basseng (etter G1) — ikke turbin-inntak.
        new("liavatn", TopologyStrategy.MultiDam,
        [
            new("liavatn_revsvatn",        "Revsvatn",         CascadePosition: 1, IsTurbineIntake: false),
            new("liavatn_nodlandsvatn",    "Nodlandsvatn",     CascadePosition: 2, IsTurbineIntake: false),
            new("liavatn_stokkurhølen",    "Stokkurhølen",     CascadePosition: 3, IsTurbineIntake: true),
            new("liavatn_liavatn",         "Liavatn (utløp)",  CascadePosition: 4, IsTurbineIntake: false),
        ]),

        // Øgreyfoss — mest kompleks: 7 dammer.
        //   [Botnavatn ‖ Urdalsvatn] → Bilstadvatn → [Gyavatn ‖ Teksevatn ‖ Migaravatn] → Øgreyvatn (terminal) → G1+G2
        new("ogreyfoss", TopologyStrategy.MultiDam,
        [
            new("ogreyfoss_botnavatn",     "Botnavatn",     CascadePosition: 1, IsTurbineIntake: false),
            new("ogreyfoss_urdalsvatn",    "Urdalsvatn",    CascadePosition: 1, IsTurbineIntake: false),
            new("ogreyfoss_bilstadvatn",   "Bilstadvatn",   CascadePosition: 2, IsTurbineIntake: false),
            new("ogreyfoss_gyavatn",       "Gyavatn",       CascadePosition: 3, IsTurbineIntake: false),
            new("ogreyfoss_teksevatn",     "Teksevatn",     CascadePosition: 3, IsTurbineIntake: false),
            new("ogreyfoss_migaravatn",    "Migaravatn",    CascadePosition: 3, IsTurbineIntake: false),
            new("ogreyfoss_ogreyvatn",     "Øgreyvatn",     CascadePosition: 4, IsTurbineIntake: true),
        ]),
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("PlantTopologySeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        try
        {
            foreach (var topology in Topologies)
            {
                if (topology.Strategy == TopologyStrategy.SingleDam)
                {
                    await SeedSingleDamAsync(db, topology, logger, ct).ConfigureAwait(false);
                }
                else
                {
                    await SeedMultiDamCascadeAsync(db, topology, logger, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            logger.LogWarning(
                "PlantTopologySeeder: 'core.dams' finnes ikke ennå. Restart etter at skjemaet er på plass.");
        }
    }

    /// <summary>
    /// Single-dam-strategi: oppdaterer kun Name + CascadePosition + IsTurbineIntake
    /// på den eksisterende <c>{plantId}_main</c>-rad. Beholder dam_id slik at
    /// signal_map-FK-er forblir gyldige.
    /// </summary>
    private static async Task SeedSingleDamAsync(
        KraftverkDbContext db, PlantTopology topology, ILogger logger, CancellationToken ct)
    {
        if (topology.Dams.Length != 1)
        {
            throw new InvalidOperationException(
                $"SingleDam-strategi krever nøyaktig 1 dam — {topology.PlantId} har {topology.Dams.Length}.");
        }

        var spec = topology.Dams[0];
        var existingMain = await db.Dams
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                d => d.PlantId == topology.PlantId && d.DamId == spec.DamId, ct)
            .ConfigureAwait(false);

        if (existingMain is null)
        {
            // DefaultDamSeeder skulle ha laget den; logg og hopp over.
            logger.LogDebug(
                "PlantTopology: {PlantId} mangler default-dam '{DamId}', hopper over (DefaultDamSeeder kjørte ikke?).",
                topology.PlantId, spec.DamId);
            return;
        }

        var changed = false;
        if (existingMain.Name != spec.Name) { existingMain.Name = spec.Name; changed = true; }
        if (existingMain.CascadePosition != spec.CascadePosition)
        { existingMain.CascadePosition = spec.CascadePosition; changed = true; }
        if (existingMain.IsTurbineIntake != spec.IsTurbineIntake)
        { existingMain.IsTurbineIntake = spec.IsTurbineIntake; changed = true; }

        if (changed)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "PlantTopology: oppdaterte single-dam for {PlantId} → '{Name}'.",
                topology.PlantId, spec.Name);
        }
    }

    /// <summary>
    /// Multi-dam-strategi: fjerner <c>{plantId}_main</c> hvis den finnes alene
    /// (mønster fra Haukland/Lindland), så setter inn navngitte dammer som
    /// ikke allerede finnes. Idempotent.
    /// </summary>
    private static async Task SeedMultiDamCascadeAsync(
        KraftverkDbContext db, PlantTopology topology, ILogger logger, CancellationToken ct)
    {
        var existingDams = await db.Dams
            .IgnoreQueryFilters()
            .Where(d => d.PlantId == topology.PlantId)
            .Select(d => d.DamId)
            .ToListAsync(ct).ConfigureAwait(false);
        var existingSet = existingDams.ToHashSet(StringComparer.Ordinal);

        // Fjern default-dammen hvis den finnes og kaskaden ikke allerede er
        // seedet (sjekk på første navngitte dam som proxy).
        var defaultDamId = $"{topology.PlantId}_main";
        var firstNamedDamId = topology.Dams[0].DamId;

        if (existingSet.Contains(defaultDamId) && !existingSet.Contains(firstNamedDamId))
        {
            var defaultDam = await db.Dams
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    d => d.PlantId == topology.PlantId && d.DamId == defaultDamId, ct)
                .ConfigureAwait(false);
            if (defaultDam is not null)
            {
                db.Dams.Remove(defaultDam);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                existingSet.Remove(defaultDamId);
                logger.LogInformation(
                    "PlantTopology: fjernet default '{DefaultDamId}' for {PlantId} før kaskade-innsetting.",
                    defaultDamId, topology.PlantId);
            }
        }

        var added = 0;
        foreach (var spec in topology.Dams)
        {
            if (existingSet.Contains(spec.DamId)) continue;
            db.Dams.Add(new DamEntry
            {
                PlantId = topology.PlantId,
                DamId = spec.DamId,
                Name = spec.Name,
                CascadePosition = spec.CascadePosition,
                IsTurbineIntake = spec.IsTurbineIntake,
                HrvMoh = null,
                LrvMoh = null,
                VolumeMm3 = null,
                OwnerOrgId = OwnerOrgId,
            });
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "PlantTopology: seedet {Count} dammer for {PlantId}-kaskade.",
                added, topology.PlantId);
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
