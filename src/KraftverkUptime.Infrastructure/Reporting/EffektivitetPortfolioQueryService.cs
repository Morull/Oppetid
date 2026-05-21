using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.Effektivitet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Portefølje-blikk på effektivitet (Spec FORBEDRINGSFORSLAG-EFFEKTIVITET.md Del 4.1).
///
/// Henter effektivitets- og episode-data for ALLE anlegg i parallell og
/// returnerer én rad per anlegg med Snitt η, sweet-spot, SVF, antall episoder,
/// og total tapt verdi. Drifts-lederen ser med ett blikk hvilket anlegg som
/// har mest å hente og kan klikke seg ned dit.
///
/// Parallelliseringen bruker scope-per-anlegg (siden DbContext er scoped og
/// ikke trådsikker). Faktor 4 samtidige som rimelig kompromiss mot pool-press.
/// </summary>
public sealed class EffektivitetPortfolioQueryService
{
    private const int MaxParallel = 4;

    private readonly IServiceProvider _services;
    private readonly KraftverkDbContext _db;
    private readonly ILogger<EffektivitetPortfolioQueryService> _log;

    public EffektivitetPortfolioQueryService(
        IServiceProvider services,
        KraftverkDbContext db,
        ILogger<EffektivitetPortfolioQueryService> log)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<IReadOnlyList<EffektivitetPortfolioRad>> GetAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        // Hent alle aktive anlegg-id'er. Holder spørringen enkel — fanger
        // alle, så filtrerer vi i UI hvis nødvendig.
        var plantIds = await _db.Plants.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        var sem = new SemaphoreSlim(MaxParallel, MaxParallel);
        try
        {
            var tasks = plantIds.Select(p => RunForPlantAsync(p.Id, p.Name, fromUtc, toUtc, sem, ct)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.Where(r => r is not null).Select(r => r!).ToList();
        }
        finally
        {
            sem.Dispose();
        }
    }

    private async Task<EffektivitetPortfolioRad?> RunForPlantAsync(
        string plantId, string plantName,
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Hver iterasjon får sin egen scope så vi får friske scoped
            // tjenester (DbContext er ikke trådsikker — kan ikke deles).
            await using var scope = _services.CreateAsyncScope();
            var episodeSvc = scope.ServiceProvider.GetRequiredService<EffektivitetEpisodeQueryService>();
            var effSvc = scope.ServiceProvider.GetRequiredService<IEffectivityQueryService>();

            var eff = await effSvc.GetAsync(plantId, fromUtc, toUtc, ct).ConfigureAwait(false);
            if (eff.DataMissing)
            {
                return new EffektivitetPortfolioRad(
                    PlantId: plantId, PlantName: plantName,
                    DataMangler: true,
                    SnittEtaPct: 0, SweetSpotEffektKw: 0, SvfM3PerKwh: 0,
                    TotalProduksjonMwh: 0, AntallEpisoder: 0, TotalTaptMwh: 0, TotalTaptNok: 0);
            }

            var episoder = await episodeSvc.GetAsync(plantId, fromUtc, toUtc, opsjoner: null, ct)
                .ConfigureAwait(false);

            return new EffektivitetPortfolioRad(
                PlantId: plantId,
                PlantName: plantName,
                DataMangler: false,
                SnittEtaPct: eff.SnittEtaPct,
                SweetSpotEffektKw: eff.SweetSpotEffektKw,
                SvfM3PerKwh: eff.SnittSpesifiktVannforbrukM3PerKwh,
                TotalProduksjonMwh: eff.TotalProduksjonKwh / 1000.0,
                AntallEpisoder: episoder.Episoder.Count,
                TotalTaptMwh: episoder.TotalTaptMwh,
                TotalTaptNok: episoder.TotalTaptNok);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Effektivitet-portefølje feilet for {PlantId} — hopper over anlegget.", plantId);
            return null;
        }
        finally
        {
            sem.Release();
        }
    }
}

/// <summary>
/// Én rad i Effektivitet-portefølje-tabellen. <see cref="DataMangler"/> = true
/// indikerer at SCADA-tags eller -samples manglet i perioden.
/// </summary>
public sealed record EffektivitetPortfolioRad(
    string PlantId,
    string PlantName,
    bool DataMangler,
    double SnittEtaPct,
    double SweetSpotEffektKw,
    double SvfM3PerKwh,
    double TotalProduksjonMwh,
    int AntallEpisoder,
    double TotalTaptMwh,
    double TotalTaptNok);
