using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.Effektivitet;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Portefølje-blikk på effektivitet (Spec FORBEDRINGSFORSLAG-EFFEKTIVITET.md Del 4.1).
///
/// For hvert anlegg fetcher vi effektivitets-respons + spotpriser EN gang,
/// og kjører episode-analyseren TO ganger:
///   – Baseline-modus  : operativt avvik fra bin-snitt (= hva som bør fikses).
///   – Sweet-spot-modus: strategisk potensial mot toppen (= hva som kan optimaliseres).
/// Begge tallene returneres per anlegg, og topp 10 episoder mot baseline
/// (de mest action-able) returneres aggregert på tvers av alle anlegg.
///
/// Parallelliseringen bruker scope-per-anlegg (siden DbContext er scoped og
/// ikke trådsikker). Faktor 4 samtidige som rimelig kompromiss mot pool-press.
/// </summary>
public sealed class EffektivitetPortfolioQueryService
{
    private const int MaxParallel = 4;
    private const int TopEpisoderCount = 10;

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

    public async Task<EffektivitetPortfolioResponse> GetAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var plantIds = await _db.Plants.AsNoTracking()
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        var sem = new SemaphoreSlim(MaxParallel, MaxParallel);
        try
        {
            var tasks = plantIds.Select(p => RunForPlantAsync(p.Id, p.Name, fromUtc, toUtc, sem, ct)).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var valid = results.Where(r => r is not null).Select(r => r!).ToList();

            // Topp 10 baseline-episoder på tvers av alle anlegg, sortert
            // synkende på tapt verdi. Baseline siden det er disse som er
            // faktisk action-able ("vi gjorde det dårligere enn vi pleier").
            var topEpisoder = valid
                .SelectMany(v => v.BaselineEpisoder.Select(ep => new TopPortfolioEpisode(
                    PlantId: v.Row.PlantId,
                    PlantName: v.Row.PlantName,
                    Episode: ep)))
                .OrderByDescending(t => t.Episode.TaptNok > 0 ? t.Episode.TaptNok : t.Episode.TaptMwh * 1000.0)
                .Take(TopEpisoderCount)
                .ToList();

            return new EffektivitetPortfolioResponse(
                Rader: valid.Select(v => v.Row).ToList(),
                TopBaselineEpisoder: topEpisoder);
        }
        finally
        {
            sem.Dispose();
        }
    }

    private async Task<PlantRun?> RunForPlantAsync(
        string plantId, string plantName,
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var scope = _services.CreateAsyncScope();
            var episodeQuery = scope.ServiceProvider.GetRequiredService<EffektivitetEpisodeQueryService>();
            var episodeService = scope.ServiceProvider.GetRequiredService<IEffektivitetEpisodeService>();

            var (eff, priser) = await episodeQuery.LoadRawDataAsync(plantId, fromUtc, toUtc, ct)
                .ConfigureAwait(false);

            if (eff.DataMissing)
            {
                return new PlantRun(
                    Row: new EffektivitetPortfolioRad(
                        PlantId: plantId, PlantName: plantName, DataMangler: true,
                        SnittEtaPct: 0, SweetSpotEffektKw: 0, SvfM3PerKwh: 0,
                        TotalProduksjonMwh: 0,
                        AntallEpisoderBaseline: 0, TotalTaptMwhBaseline: 0, TotalTaptNokBaseline: 0,
                        AntallEpisoderSweetSpot: 0, TotalTaptMwhSweetSpot: 0, TotalTaptNokSweetSpot: 0),
                    BaselineEpisoder: Array.Empty<UnderytendeEpisode>());
            }

            // Kjør analyseren TO ganger med samme rådata — én gang per
            // referanse. Analyzer er ren funksjon så dette er nesten gratis.
            var baselineResult = episodeService.Analyse(eff, priser,
                new EpisodeAnalyseOpsjoner(Referanse: EpisodeReferanseTyp.Baseline));
            var sweetSpotResult = episodeService.Analyse(eff, priser,
                new EpisodeAnalyseOpsjoner(Referanse: EpisodeReferanseTyp.SweetSpot));

            return new PlantRun(
                Row: new EffektivitetPortfolioRad(
                    PlantId: plantId,
                    PlantName: plantName,
                    DataMangler: false,
                    SnittEtaPct: eff.SnittEtaPct,
                    SweetSpotEffektKw: eff.SweetSpotEffektKw,
                    SvfM3PerKwh: eff.SnittSpesifiktVannforbrukM3PerKwh,
                    TotalProduksjonMwh: eff.TotalProduksjonKwh / 1000.0,
                    AntallEpisoderBaseline: baselineResult.Episoder.Count,
                    TotalTaptMwhBaseline: baselineResult.TotalTaptMwh,
                    TotalTaptNokBaseline: baselineResult.TotalTaptNok,
                    AntallEpisoderSweetSpot: sweetSpotResult.Episoder.Count,
                    TotalTaptMwhSweetSpot: sweetSpotResult.TotalTaptMwh,
                    TotalTaptNokSweetSpot: sweetSpotResult.TotalTaptNok),
                BaselineEpisoder: baselineResult.Episoder);
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

    /// <summary>Internt par av rad-DTO og rå-episode-liste for topp-aggregering.</summary>
    private sealed record PlantRun(
        EffektivitetPortfolioRad Row,
        IReadOnlyList<UnderytendeEpisode> BaselineEpisoder);
}

/// <summary>
/// Toppnivå-respons fra portefølje-endepunktet — én rad per anlegg +
/// topp 10 baseline-episoder på tvers av alle anlegg.
/// </summary>
public sealed record EffektivitetPortfolioResponse(
    IReadOnlyList<EffektivitetPortfolioRad> Rader,
    IReadOnlyList<TopPortfolioEpisode> TopBaselineEpisoder);

/// <summary>
/// Én av topp 10-episodene. Inneholder plant-info så UI kan vise hvilket
/// anlegg episoden gjelder, og hele <see cref="UnderytendeEpisode"/> så
/// detalj-dialogen kan åpnes direkte.
/// </summary>
public sealed record TopPortfolioEpisode(
    string PlantId,
    string PlantName,
    UnderytendeEpisode Episode);

/// <summary>
/// Én rad i Effektivitet-portefølje-tabellen. <see cref="DataMangler"/> = true
/// indikerer at SCADA-tags eller -samples manglet i perioden. Tap vises mot
/// både baseline (operativt avvik) og sweet-spot (strategisk potensial).
/// </summary>
public sealed record EffektivitetPortfolioRad(
    string PlantId,
    string PlantName,
    bool DataMangler,
    double SnittEtaPct,
    double SweetSpotEffektKw,
    double SvfM3PerKwh,
    double TotalProduksjonMwh,
    int AntallEpisoderBaseline,
    double TotalTaptMwhBaseline,
    double TotalTaptNokBaseline,
    int AntallEpisoderSweetSpot,
    double TotalTaptMwhSweetSpot,
    double TotalTaptNokSweetSpot);
