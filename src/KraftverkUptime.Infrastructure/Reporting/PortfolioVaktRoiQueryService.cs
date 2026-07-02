using KraftverkUptime.Core.Domain;
using KraftverkUptime.Core.Time;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Infrastructure.Persistence.Entities;
using KraftverkUptime.Modules.Reporting.Nedetid;
using KraftverkUptime.Modules.Reporting.Portefolje;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Implementasjon av <see cref="IPortfolioVaktRoiQueryService"/>: itererer over
/// alle anlegg, kjører <see cref="VaktRoiCalculator"/> per anlegg og aggregerer
/// resultatene. Mønster matcher <see cref="PortfolioQueryService"/>.
///
/// Anleggene kjøres i PARALLELL med scope-per-anlegg (siden DbContext er Scoped
/// og ikke tråd-sikker — hver task får ferskt DbContext + ferske sub-tjenester),
/// begrenset til <see cref="MaxParallel"/> samtidige mot DB-pool-press. Dette er
/// den tyngste delkomponenten i /economy (som kaller denne) i tillegg til selve
/// vakt-roi-rapporten. Aggregeringen skjer sekvensielt etter at alle anlegg er
/// ferdige, så resultatet er identisk med en sekvensiell kjøring. Malen er den
/// samme som <see cref="EffektivitetPortfolioQueryService"/>.
/// </summary>
public sealed class PortfolioVaktRoiQueryService : IPortfolioVaktRoiQueryService
{
    private const int MaxParallel = 4;

    private readonly IServiceProvider _services;
    private readonly KraftverkDbContext _db;
    private readonly ILogger<PortfolioVaktRoiQueryService> _log;

    public PortfolioVaktRoiQueryService(
        IServiceProvider services,
        KraftverkDbContext db,
        ILogger<PortfolioVaktRoiQueryService> log)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<PortfolioVaktRoiResponse> GetAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        int topN, VaktTidsmodellOptions? vaktOptions, CancellationToken ct)
    {
        if (toUtc <= fromUtc)
        {
            return new PortfolioVaktRoiResponse(
                fromUtc, toUtc,
                PlantCount: 0, PlantsWithData: 0,
                TotalReddetNok: 0, TotalReddbareEvents: 0, TotalEvents: 0,
                PerPlant: Array.Empty<PortfolioVaktRoiPlantSummary>(),
                TopEvents: Array.Empty<PortfolioVaktRoiTopEvent>(),
                MonthlyTrend: Array.Empty<PortfolioVaktRoiMonthlyPoint>());
        }

        var topNCapped = Math.Clamp(topN, 1, 100);

        var plants = await _db.Plants
            .OrderBy(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        // Parallell per-anlegg med scope-per-anlegg (DbContext ikke tråd-trygg).
        var sem = new SemaphoreSlim(MaxParallel, MaxParallel);
        PlantVaktRoiResult?[] gathered;
        try
        {
            var tasks = plants
                .Select(p => RunPlantAsync(p, fromUtc, toUtc, vaktOptions, sem, ct))
                .ToList();
            gathered = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            sem.Dispose();
        }

        // Aggregering sekvensielt — resultatlista bevarer anleggs-rekkefølge
        // (Task.WhenAll beholder rekkefølgen på tasks), null = anlegg uten data.
        var results = gathered.Where(r => r is not null).Select(r => r!).ToList();

        var perPlant = results.Select(r => r.Summary).ToList();
        var plantsWithData = results.Count;
        var totalReddetNok = results.Sum(r => r.Summary.ReddetNok);
        var totalReddbare = results.Sum(r => r.Summary.ReddbareEvents);
        var totalEvents = results.Sum(r => r.Summary.TotaleEvents);

        var allTopCandidates = results.SelectMany(r => r.QualifyingEvents).ToList();

        var topEvents = allTopCandidates
            .OrderByDescending(e => e.ReddetNok)
            .Take(topNCapped)
            .ToList();

        var monthlyTrend = allTopCandidates
            .GroupBy(e =>
            {
                var local = e.StartUtc.ToLocalTime();
                return (local.Year, local.Month);
            })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new PortfolioVaktRoiMonthlyPoint(
                Year: g.Key.Year,
                Month: g.Key.Month,
                TotalReddetNok: g.Sum(e => e.ReddetNok),
                ReddbareEvents: g.Count()))
            .ToList();

        _log.LogInformation(
            "PortfolioVaktRoi: {PlantCount} anlegg, {WithData} med data — total reddet {Total:F0} NOK, {Reddbare}/{Events} events.",
            plants.Count, plantsWithData, totalReddetNok, totalReddbare, totalEvents);

        return new PortfolioVaktRoiResponse(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            PlantCount: plants.Count,
            PlantsWithData: plantsWithData,
            TotalReddetNok: totalReddetNok,
            TotalReddbareEvents: totalReddbare,
            TotalEvents: totalEvents,
            PerPlant: perPlant
                .OrderByDescending(p => p.ReddetNok)
                .ToList(),
            TopEvents: topEvents,
            MonthlyTrend: monthlyTrend);
    }

    private async Task<PlantVaktRoiResult?> RunPlantAsync(
        PlantRegistration plant,
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        VaktTidsmodellOptions? vaktOptions,
        SemaphoreSlim sem, CancellationToken ct)
    {
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Ferskt scope per anlegg → eget DbContext + ferske sub-tjenester.
            // IUptimeReportStore (singleton, caching-dekorator) deles trygt.
            await using var scope = _services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var nedetid = sp.GetRequiredService<INedetidQueryService>();
            var overflow = sp.GetRequiredService<IOverflowQueryService>();
            var inflow = sp.GetRequiredService<InflowOverflowQueryService>();
            var calculator = sp.GetRequiredService<VaktRoiCalculator>();
            var db = sp.GetRequiredService<KraftverkDbContext>();

            var events = await nedetid.ListEventsAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);
            if (events.Count == 0)
            {
                return null;
            }

            // Snitt-spotpris-proxy fra events (samme metode som per-plant-endepunktet).
            double snittSpot = 500;
            var sumTapMwh = events.Sum(e => e.TapMwh);
            var sumTapNok = events.Sum(e => e.TapNok);
            if (sumTapMwh > 0 && sumTapNok > 0) snittSpot = sumTapNok / sumTapMwh;

            // Utvid overløps-vinduet med 3 dager for å dekke counterfactual-
            // utvidelsen for events nær slutten av perioden (samme buffer som
            // plan-tjenesten bruker). Uten dette telles aldri overløp som
            // faller etter toUtc, selv om kalkulatoren ser disse timene.
            var dataset = await overflow.GetOverflowDatasetAsync(plant.Id, fromUtc, toUtc.AddDays(3), ct)
                .ConfigureAwait(false);
            var snittUbalansetillegg = await nedetid.GetAvgImbalancePremiumAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);
            // Variant 2 (SPEC-VAKT-ROI-UBALANSE-FULLPERIODE-OG-VISNING): per-time
            // premie; snittet over er fallback for timer uten pris-data.
            var ubalansePremieByHour = await nedetid.GetImbalancePremiumByHourAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);
            var planResult = await nedetid.GetProduksjonplanByHourAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);

            // Per-anlegg overrides — henter hele raden siden vi trenger
            // både Classification og ActualEndOverrideUtc (varighetsoverstyring,
            // B1 2026-05-20). Varighet anvendes oppstrøms; klassifisering
            // sendes inn i Calculator som vanlig.
            var overrideRows = await db.VaktEventOverrides
                .Where(o => o.PlantId == plant.Id
                    && o.EventStartUtc >= fromUtc
                    && o.EventStartUtc < toUtc)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // START-korreksjoner FØRST (SPEC-NEDETID-STARTTID-OVERRIDE): effektiv
            // StartUtc + bevart detektert start; varighet/tap reberegnes fra plan.
            var startOverrides = overrideRows
                .Where(o => o.ActualStartOverrideUtc.HasValue)
                .ToDictionary(o => o.EventStartUtc, o => o.ActualStartOverrideUtc!.Value);
            events = StartOverrideApplier.Apply(
                events, startOverrides, planResult.PlanByHour, fallbackSpotNokMwh: snittSpot);

            if (overrideRows.Any(o => o.ActualEndOverrideUtc.HasValue))
            {
                var endOverrides = overrideRows
                    .Where(o => o.ActualEndOverrideUtc.HasValue)
                    .ToDictionary(o => o.EventStartUtc, o => o.ActualEndOverrideUtc!.Value);

                events = events
                    // Oppslag på DETEKTERT start (override-radens nøkkel).
                    .Select(e => endOverrides.TryGetValue(e.EffektivDetectedStartUtc, out var endOv)
                        ? e with { EndUtc = endOv }
                        : e)
                    .ToList();
            }

            // Kalkulatoren slår opp på leder-eventets EFFEKTIVE StartUtc —
            // oversett rad-nøklene (detektert start) via events-listen.
            var detectedToEffective = events.ToDictionary(
                e => e.EffektivDetectedStartUtc, e => e.StartUtc);
            var overrides = overrideRows
                .Where(o => o.Classification != "Auto")
                .ToDictionary(
                    o => detectedToEffective.TryGetValue(o.EventStartUtc, out var eff)
                        ? eff : o.EventStartUtc,
                    o => o.Classification);

            // U2-PlanDeviation-filter (spec NESTE-CHAT-VAKTROI-PLANDEVIATION-FILTER.md,
            // 2026-05-22): hendelser uten operlog-match og uten eksplisitt Yes-override
            // teller ikke som vakt-utrykning. Bygges per-plant før kalkulatoren kalles.
            var guardOverridesByEventStart = overrideRows
                .ToDictionary(o => o.EventStartUtc, o => o.GuardResponseOverride);
            var excludeFromReddbar = events
                .Where(e =>
                {
                    // Vakt-utrykning-raden er nøklet på DETEKTERT start.
                    var ovr = guardOverridesByEventStart.TryGetValue(e.EffektivDetectedStartUtc, out var g)
                        ? (GuardResponseOverride?)g
                        : null;
                    return !EffectiveGuardResponseEvaluator.ShouldCount(e, ovr);
                })
                .Select(e => e.StartUtc)
                .ToHashSet();

            // Dam-telemetri for tilsig-basert counterfactual-overløp (SPEC-VAKT-
            // ROI-OVERLOP-V2). Null for anlegg uten magasin-telemetri → kun observert.
            var damTelemetry = await inflow
                .GetDamTelemetryAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);

            var roi = calculator.Calculate(
                events, snittSpot, planResult.PlanByHour,
                dataset.OverflowHours, overflowDataAvailable: dataset.DataAvailable,
                snittUbalansetillegg_NokMwh: snittUbalansetillegg,
                overrides: overrides,
                proxyHours: planResult.ProxyHours,
                vaktOptions: vaktOptions,
                excludeFromReddbar: excludeFromReddbar,
                damSamples: damTelemetry?.Samples,
                maxVolumeM3: damTelemetry?.MaxVolumeM3 ?? 0,
                fillRateByHour: damTelemetry?.FillRateByHour,
                ubalansePremieByHour: ubalansePremieByHour);

            var plantReddetNok = roi.Sum(r => r.ReddetNok);
            var plantReddetProduksjon = roi.Sum(r => r.ReddetProduksjon_NOK);
            var plantReddetUbalanse = roi.Sum(r => r.ReddetUbalanse_NOK);
            var plantReddbare = roi.Count(r => r.ErInnenforVakt && r.ErReddbar);

            var summary = new PortfolioVaktRoiPlantSummary(
                PlantId: plant.Id,
                PlantName: plant.Name,
                InstalledCapacityMw: plant.InstalledCapacityMw,
                ReddetNok: plantReddetNok,
                ReddetProduksjon_NOK: plantReddetProduksjon,
                ReddetUbalanse_NOK: plantReddetUbalanse,
                ReddbareEvents: plantReddbare,
                TotaleEvents: events.Count);

            // Top-N-kandidater + månedstrend: kun events som faktisk reddet noe.
            var qualifying = roi
                .Where(r => r.ReddetNok > 0)
                .Select(r => new PortfolioVaktRoiTopEvent(
                    PlantId: plant.Id,
                    PlantName: plant.Name,
                    StartUtc: r.Event.StartUtc,
                    EndUtc: r.Event.EndUtc,
                    VarighetTimer: r.Event.VarighetTimer,
                    Kategori: r.Event.Category.ToString(),
                    CauseCode: r.Event.CauseCode,
                    ReddetNok: r.ReddetNok,
                    ReddetProduksjon_NOK: r.ReddetProduksjon_NOK,
                    ReddetUbalanse_NOK: r.ReddetUbalanse_NOK,
                    EkstraTimerSpart: r.EkstraTimerSpart))
                .ToList();

            return new PlantVaktRoiResult(summary, qualifying);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "PortfolioVaktRoi: anlegg {PlantId} feilet — hopper over anlegget.", plant.Id);
            return null;
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>Internt per-anlegg-resultat for sekvensiell aggregering etter fan-out.</summary>
    private sealed record PlantVaktRoiResult(
        PortfolioVaktRoiPlantSummary Summary,
        List<PortfolioVaktRoiTopEvent> QualifyingEvents);
}
