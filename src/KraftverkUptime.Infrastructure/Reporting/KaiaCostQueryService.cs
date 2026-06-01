using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.KaiaCost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// EF-basert implementasjon av <see cref="IKaiaCostQueryService"/>.
/// Leser meglerprovisjon fra <c>core.settlement_imports.meglerprovisjon_nok</c>
/// og pro-rata fast avgift fra <c>core.plants.kaia_annual_fee_nok</c>.
///
/// Pro-rata-beregningen er isolert i <see cref="KaiaFeeProration.Compute"/>
/// slik at den kan unit-testes uten DI.
///
/// Spec: <c>docs/SPEC-KAIA-KOSTNAD.md</c>.
/// </summary>
public sealed class KaiaCostQueryService : IKaiaCostQueryService
{
    private readonly KraftverkDbContext _db;
    private readonly ILogger<KaiaCostQueryService> _log;

    public KaiaCostQueryService(KraftverkDbContext db, ILogger<KaiaCostQueryService> log)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<KaiaCostResult?> GetForImportAsync(
        string plantId, string idempotencyKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var import = await _db.SettlementImports.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.PlantId == plantId && x.IdempotencyKey == idempotencyKey, ct)
            .ConfigureAwait(false);

        if (import is null)
        {
            return null;
        }

        var plant = await _db.Plants.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == plantId, ct).ConfigureAwait(false);

        var annualFee = plant?.KaiaAnnualFeeNok ?? 4000d;
        return BuildResult(
            plantId: plantId,
            plantName: import.PlantName,
            periodStart: import.PeriodStartUtc,
            periodEnd: import.PeriodEndUtc,
            meglerprovisjonSource: import.MeglerprovisjonNok,
            annualFee: annualFee);
    }

    public async Task<IReadOnlyList<KaiaCostResult>> GetForPortfolioAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        if (toUtc <= fromUtc)
        {
            return Array.Empty<KaiaCostResult>();
        }

        // Hent alle ikke-slettede importer som overlapper [from, to]. Per
        // (anlegg, periode) plukker vi den nyeste import (reimport overstyrer
        // gammel). Periode-overlapp brukes for å støtte delvise måneder; sum
        // over flere måneder er korrekt fordi v1 har én import = én måned.
        var imports = await _db.SettlementImports.AsNoTracking()
            .Where(x => x.PlantId != null
                        && x.DeletedAt == null
                        && x.PeriodStartUtc < toUtc
                        && x.PeriodEndUtc > fromUtc)
            .OrderByDescending(x => x.ImportedAtUtc)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (imports.Count == 0)
        {
            return Array.Empty<KaiaCostResult>();
        }

        // Dedupliser PER ANLEGG på overlappende perioder — ikke bare eksakt
        // like (PeriodStart, PeriodEnd). Ellers dobbelttelles KAIA-kostnad når
        // samme måned er re-importert med ulik datospenn (f.eks. «mai 1–17» +
        // «mai 1–20»). Gir fortsatt én rad per (plant, måned). Se
        // OverlappingImportResolver.
        var latestPerPlantPeriod = imports
            .GroupBy(x => x.PlantId!)
            .SelectMany(g => OverlappingImportResolver.ResolveNonOverlapping(
                g,
                x => x.PeriodStartUtc,
                x => x.PeriodEndUtc,
                x => x.ImportedAtUtc))
            .ToList();

        var plantIds = latestPerPlantPeriod
            .Select(x => x.PlantId!)
            .Distinct()
            .ToList();
        var plants = await _db.Plants.AsNoTracking()
            .Where(p => plantIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct)
            .ConfigureAwait(false);

        var results = new List<KaiaCostResult>(latestPerPlantPeriod.Count);
        foreach (var import in latestPerPlantPeriod)
        {
            var plantId = import.PlantId!;
            var plant = plants.GetValueOrDefault(plantId);
            var annualFee = plant?.KaiaAnnualFeeNok ?? 4000d;

            results.Add(BuildResult(
                plantId: plantId,
                plantName: import.PlantName,
                periodStart: import.PeriodStartUtc,
                periodEnd: import.PeriodEndUtc,
                meglerprovisjonSource: import.MeglerprovisjonNok,
                annualFee: annualFee));
        }

        return results
            .OrderBy(r => r.PlantName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.PeriodStartUtc)
            .ToList();
    }

    /// <summary>
    /// Bygger resultatet fra rå-data: snur fortegnet på meglerprovisjon (kilden
    /// er negativ → positiv kostnad), pro-rata fast avgift, summerer total.
    /// </summary>
    private static KaiaCostResult BuildResult(
        string plantId,
        string plantName,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        double? meglerprovisjonSource,
        double annualFee)
    {
        // Snu fortegn: kilden er negativ (KAIA tar fra oss), kostnad er positiv.
        double? meglerprovisjonCost = meglerprovisjonSource.HasValue
            ? -meglerprovisjonSource.Value
            : null;

        var fastAvgift = KaiaFeeProration.Compute(annualFee, periodStart, periodEnd);

        double? total = meglerprovisjonCost.HasValue
            ? meglerprovisjonCost.Value + fastAvgift
            : null;

        var note = meglerprovisjonSource is null
            ? "Meglerprovisjon mangler i eksport — kjør reimport for full kostnad."
            : null;

        return new KaiaCostResult
        {
            PlantId = plantId,
            PlantName = plantName,
            PeriodStartUtc = periodStart,
            PeriodEndUtc = periodEnd,
            MeglerprovisjonNok = meglerprovisjonCost,
            FastAvgiftNok = fastAvgift,
            TotalNok = total,
            Note = note,
        };
    }
}
