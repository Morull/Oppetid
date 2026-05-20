using KraftverkUptime.Core.Storage;
using KraftverkUptime.Modules.Settlement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Persistence;

/// <summary>
/// Idempotent backfill av <c>core.settlement_imports.meglerprovisjon_nok</c>
/// for rader importert FØR KAIA-kostnad-kolonnen ble lagt til (Spec
/// KAIA-KOSTNAD steg 8). Leser opprinnelig Excel-blob, re-parser, og henter
/// <c>Summary.MeglerprovisjonNok</c> (eller summer hourly hvis Summary er null).
///
/// Hopper over rader som allerede har verdi — kan trygt kjøres flere ganger.
/// Hopper også over rader der blobben er borte (loggføres).
///
/// Sliter ikke import-fila: ingen sletting eller endring i blob-lagringen,
/// kun en kolonneoppdatering på import-raden.
/// </summary>
public static class KaiaMeglerprovisjonBackfillSeeder
{
    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("KaiaMeglerprovisjonBackfillSeeder");
        var db = scope.ServiceProvider.GetRequiredService<KraftverkDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var parser = scope.ServiceProvider.GetRequiredService<ISettlementParser>();

        // Hopp ut tidlig hvis kolonnen ikke finnes (helt fresh DB der schema-broen
        // ikke kjørte av en eller annen grunn).
        List<Entities.SettlementImport> rader;
        try
        {
            rader = await db.SettlementImports
                .Where(x => x.MeglerprovisjonNok == null && x.DeletedAt == null)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "KAIA backfill: hoppet over (kolonnen finnes ikke eller tabellen er tom).");
            return;
        }

        if (rader.Count == 0)
        {
            logger.LogDebug("KAIA backfill: alle import-rader har allerede meglerprovisjon_nok.");
            return;
        }

        logger.LogInformation(
            "KAIA backfill: starter — {Count} import-rader uten meglerprovisjon_nok.",
            rader.Count);

        // Grupper rader på blob-sti slik at vi parser hver fil kun én gang —
        // multi-plant-arbeidsbøker har samme blob for 9–11 plant-rader, og en
        // re-parsing per rad er sløsing av tid og kan trigge "summary første
        // plant for alle rader"-feilen som vi tidligere så.
        var rowsByBlob = rader.GroupBy(r => r.BlobPath).ToList();

        var oppdatert = 0;
        var hoppetOver = 0;
        var feilet = 0;
        foreach (var blobGroup in rowsByBlob)
        {
            var blobPath = blobGroup.Key;
            var blobRows = blobGroup.ToList();

            IReadOnlyList<Modules.Settlement.Dtos.ParsedSettlement> parsedAll;
            try
            {
                await using var stream = await storage.GetAsync(blobPath, ct).ConfigureAwait(false);
                parsedAll = await parser.ParseAllAsync(stream, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                hoppetOver += blobRows.Count;
                logger.LogWarning(
                    "KAIA backfill: blob {Blob} mangler — hopper over {Count} rader.",
                    blobPath, blobRows.Count);
                continue;
            }
            catch (Exception ex)
            {
                feilet += blobRows.Count;
                logger.LogWarning(ex,
                    "KAIA backfill: feilet å parse {Blob} — hopper over {Count} rader.",
                    blobPath, blobRows.Count);
                continue;
            }

            foreach (var rad in blobRows)
            {
                try
                {
                    // For multi-plant: match parsed.PlantId mot rad.PlantId
                    // (kanonisk slug). For single-plant: parsedAll har én entry
                    // med PlantId=null — bruk den.
                    var parsed = parsedAll.Count == 1 && parsedAll[0].PlantId is null
                        ? parsedAll[0]
                        : parsedAll.FirstOrDefault(p =>
                            string.Equals(p.PlantId, rad.PlantId,
                                StringComparison.OrdinalIgnoreCase));

                    if (parsed is null)
                    {
                        hoppetOver++;
                        logger.LogDebug(
                            "KAIA backfill: {Plant} ikke funnet i {Blob} (multi-plant uten matchende fane).",
                            rad.PlantId, blobPath);
                        continue;
                    }

                    // Foretrekker summary-raden (én kilde i fila). Multi-plant-
                    // arbeidsbøker mangler Summary → fall tilbake til sum av hourly.
                    double? meglerprovisjon = parsed.Summary?.MeglerprovisjonNok;
                    if (meglerprovisjon is null)
                    {
                        double sum = 0;
                        var any = false;
                        foreach (var h in parsed.Hourly)
                        {
                            if (h.MeglerprovisjonNok.HasValue)
                            {
                                sum += h.MeglerprovisjonNok.Value;
                                any = true;
                            }
                        }
                        meglerprovisjon = any ? sum : null;
                    }

                    if (meglerprovisjon is null)
                    {
                        hoppetOver++;
                        logger.LogDebug(
                            "KAIA backfill: {Plant}/{Key} har ingen meglerprovisjon.",
                            rad.PlantId, rad.IdempotencyKey);
                        continue;
                    }

                    rad.MeglerprovisjonNok = meglerprovisjon;
                    oppdatert++;
                }
                catch (Exception ex)
                {
                    feilet++;
                    logger.LogWarning(ex,
                        "KAIA backfill: feilet for {Plant}/{Key} (blob {Blob}).",
                        rad.PlantId, rad.IdempotencyKey, blobPath);
                }
            }
        }

        if (oppdatert > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        logger.LogInformation(
            "KAIA backfill ferdig: {Oppdatert} oppdatert, {Hoppet} hoppet over, {Feilet} feilet.",
            oppdatert, hoppetOver, feilet);
    }
}
