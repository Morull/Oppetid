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

    /// <summary>Anleggene som auto-opprettes. PlantId genereres via <see cref="PlantSlug.ToSlug"/>.</summary>
    private static readonly string[] PortfolioNames =
    [
        "Løgjen",
        "Drivdal",
        "Grødemfoss",
        "Haukland",
        "Honnefoss",
        "Lindland",
        "Øgreyfoss",
        "Ørsdalen",
        "Liavatn",
        "Vikeså",
        "Stølskraft",
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

        foreach (var name in PortfolioNames)
        {
            var id = PlantSlug.ToSlug(name);
            if (string.IsNullOrEmpty(id) || existing.Contains(id)) continue;

            db.Plants.Add(new PlantRegistration
            {
                Id = id,
                OwnerOrgId = OwnerOrgId,
                Name = name,
                Type = PlantType.Regulated, // default for Dalane Kraft-porteføljen
                InstalledCapacityMw = 0, // placeholder — oppdateres manuelt
                TimeZone = "Europe/Oslo",
            });
            added++;
            logger.LogInformation(
                "Bootstrappet anlegg {PlantId} ({Name}) — InstalledCapacityMw=0 (krever manuell oppdatering).",
                id, name);
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
