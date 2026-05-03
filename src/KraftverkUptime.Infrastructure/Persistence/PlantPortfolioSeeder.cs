using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Bootstrap-seeder for hele Dalane Kraft-porteføljen (11 anlegg). Idempotent —
/// hopper over anlegg som allerede finnes i <c>core.plants</c>.
///
/// <see cref="PlantRegistration.InstalledCapacityMw"/> settes til 0 ved auto-
/// opprettelse fordi datakilden ikke har effekt-info. Brukeren oppdaterer
/// manuelt etter at fysisk effekt er bekreftet for hvert anlegg. Vakt-ROI
/// gir 0 NOK for anlegg med 0 kapasitet, så det blir tydelig hva som mangler
/// oppsett.
///
/// Drivdal er eksisterende — seederen oppdaterer ikke navnet hvis det er endret.
/// </summary>
public static class PlantPortfolioSeeder
{
    private const string OwnerOrgId = "dev-org";

    /// <summary>
    /// Anleggene som auto-opprettes. PlantId genereres via <see cref="PlantSlug.ToSlug"/>.
    /// <c>InstalledCapacityMw</c> oppgitt fra drifts-leder Dalane Kraft 2026-04-29.
    /// Løgjen har vi ikke sertifisert effekt for ennå — settes til 0 og må oppdateres
    /// manuelt før Vakt-ROI gir meningsfulle tall for det anlegget.
    ///
    /// <c>Type</c> bekreftet av drifts-leder 2026-05-02 (SPEC-MVP-HARDENING D):
    ///   - Lindland klassifiseres som RunOfRiver fordi 24t-lag fra magasin gjør
    ///     drift hydrologi-styrt i praksis (selv om det fysisk er kaskade).
    ///   - Ørsdalen er ren elvekraft.
    ///   - Vikeså/Stølskraft er Mixed (lite magasin / vannforbruks-styrt).
    ///   - Resten er Regulated (kaskader med betydelige magasin).
    /// </summary>
    private static readonly (string Name, double CapacityMw, PlantType Type)[] Portfolio =
    [
        ("Løgjen",     0,    PlantType.Regulated),  // magasin
        ("Drivdal",    2.3,  PlantType.Regulated),
        ("Grødemfoss", 2.8,  PlantType.Regulated),  // Smievatn er magasin/inntak
        ("Haukland",   4.9,  PlantType.Regulated),  // kaskade
        ("Honnefoss",  3.1,  PlantType.Regulated),  // Kydland + Spjodevatn-magasin
        ("Lindland",   8.9,  PlantType.RunOfRiver), // 24t-lag → fungerer som elvekraft
        ("Øgreyfoss",  14.6, PlantType.Regulated),  // to generatorer, kaskade
        ("Ørsdalen",   4.0,  PlantType.RunOfRiver), // ren elvekraft
        ("Liavatn",    2.0,  PlantType.Regulated),  // kaskade
        ("Vikeså",     4.0,  PlantType.Mixed),      // lite magasin
        ("Stølskraft", 1.5,  PlantType.Mixed),      // vannforbruks-styrt (Gjesdal)
    ];

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("PlantPortfolioSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();

        List<string> existingIds;
        try
        {
            existingIds = await db.Plants
                .IgnoreQueryFilters()
                .Select(p => p.Id)
                .ToListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMissingRelation(ex))
        {
            logger.LogWarning(
                "Hopper over plant-bootstrap: 'core.plants' finnes ikke ennå.");
            return;
        }

        var existing = existingIds.ToHashSet(StringComparer.Ordinal);
        var added = 0;

        foreach (var (name, capacity, type) in Portfolio)
        {
            var id = PlantSlug.ToSlug(name);
            if (string.IsNullOrEmpty(id) || existing.Contains(id)) continue;

            db.Plants.Add(new PlantRegistration
            {
                Id = id,
                OwnerOrgId = OwnerOrgId,
                Name = name,
                Type = type,
                InstalledCapacityMw = capacity,
                TimeZone = "Europe/Oslo",
            });
            added++;
            logger.LogInformation(
                "Bootstrappet anlegg {PlantId} ({Name}) — Type={Type}, InstalledCapacityMw={Capacity}.",
                id, name, type, capacity);
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Plant-bootstrap fullført: {Count} nye anlegg opprettet.", added);
        }
        else
        {
            logger.LogDebug("Plant-bootstrap: alle anlegg eksisterer allerede.");
        }

        // Idempotent capacity-backfill: fyller inn kapasitet for anlegg som
        // ble opprettet før vi hadde tallene (eller med 0). Respekterer
        // manuelle oppdateringer — hvis capacity allerede er satt til noe
        // annet enn 0 lar vi være å overstyre.
        await BackfillCapacitiesAsync(db, logger, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Historiske én-skudds-rettelser for kapasiteter som ble persistert med
    /// feil verdi før porteføljen ble bekreftet. Anvendes kun hvis nåværende
    /// verdi matcher <c>FromValue</c> eksakt — etter rettelse er regelen
    /// idempotent (verdien er ikke lenger lik FromValue).
    /// </summary>
    private static readonly (string PlantId, double FromValue, double ToValue)[] HistoricalCapacityFixes =
    [
        ("drivdal", 2.2, 2.3), // korrigert 2026-04-29 av drifts-leder
    ];

    /// <summary>
    /// Setter <see cref="PlantRegistration.InstalledCapacityMw"/> for porteføljens
    /// anlegg som fortsatt har capacity = 0, samt anvender historiske rettelser
    /// (f.eks. Drivdal 2.2 → 2.3). Anlegg med andre kapasiteter røres ikke
    /// (respekt for manuelle oppdateringer fra brukeren).
    /// </summary>
    private static async Task BackfillCapacitiesAsync(
        KraftverkDbContext db, ILogger logger, CancellationToken ct)
    {
        var byId = Portfolio.ToDictionary(
            x => PlantSlug.ToSlug(x.Name), x => (x.Name, x.CapacityMw, x.Type),
            StringComparer.Ordinal);

        var allPlants = await db.Plants.IgnoreQueryFilters().ToListAsync(ct).ConfigureAwait(false);
        var updated = 0;

        foreach (var plant in allPlants)
        {
            // 1) Backfill capacity=0 plants fra Portfolio-arrayet
            if (plant.InstalledCapacityMw == 0
                && byId.TryGetValue(plant.Id, out var portfolio)
                && portfolio.CapacityMw > 0)
            {
                plant.InstalledCapacityMw = portfolio.CapacityMw;
                updated++;
                logger.LogInformation(
                    "Backfill InstalledCapacityMw for {PlantId} ({Name}): 0 → {Capacity} MW.",
                    plant.Id, plant.Name, portfolio.CapacityMw);
            }

            // 2) Backfill PlantType (SPEC-MVP-HARDENING D): hvis nåværende type
            // er default Regulated og spec sier noe annet, oppdater. Respekt
            // for manuelle endringer: hvis plant.Type allerede er ulik bådeRegulated
            // og spec-verdien, lar vi være å overstyre.
            if (byId.TryGetValue(plant.Id, out var entry)
                && plant.Type == PlantType.Regulated
                && entry.Type != PlantType.Regulated)
            {
                logger.LogInformation(
                    "Backfill PlantType for {PlantId} ({Name}): Regulated → {NewType}.",
                    plant.Id, plant.Name, entry.Type);
                plant.Type = entry.Type;
                updated++;
            }

            // 3) Anvend historiske én-skudds-rettelser (idempotent)
            foreach (var (fixId, fromValue, toValue) in HistoricalCapacityFixes)
            {
                if (string.Equals(plant.Id, fixId, StringComparison.Ordinal)
                    && plant.InstalledCapacityMw == fromValue)
                {
                    plant.InstalledCapacityMw = toValue;
                    updated++;
                    logger.LogInformation(
                        "Historisk kapasitets-rettelse for {PlantId} ({Name}): {From} → {To} MW.",
                        plant.Id, plant.Name, fromValue, toValue);
                    break;
                }
            }
        }

        if (updated > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Idempotent ad-hoc-opprettelse av et anlegg fra parser-output. Brukes
    /// av multi-plant-import når en fane refererer til et anlegg vi ikke har
    /// fra før. Returnerer true hvis et nytt anlegg ble opprettet.
    /// </summary>
    public static async Task<bool> EnsurePlantExistsAsync(
        KraftverkDbContext db, string plantId, string canonicalName,
        ILogger logger, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);

        var exists = await db.Plants
            .IgnoreQueryFilters()
            .AnyAsync(p => p.Id == plantId, ct)
            .ConfigureAwait(false);
        if (exists) return false;

        db.Plants.Add(new PlantRegistration
        {
            Id = plantId,
            OwnerOrgId = OwnerOrgId,
            Name = canonicalName,
            Type = PlantType.Regulated,
            InstalledCapacityMw = 0,
            TimeZone = "Europe/Oslo",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "Auto-opprettet anlegg {PlantId} ({Name}) under settlement-import — InstalledCapacityMw=0.",
            plantId, canonicalName);
        return true;
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
