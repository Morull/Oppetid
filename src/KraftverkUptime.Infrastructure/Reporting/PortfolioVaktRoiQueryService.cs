using KraftverkUptime.Core.Domain;
using KraftverkUptime.Infrastructure.Persistence;
using KraftverkUptime.Modules.Reporting.Nedetid;
using KraftverkUptime.Modules.Reporting.Portefolje;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Reporting;

/// <summary>
/// Implementasjon av <see cref="IPortfolioVaktRoiQueryService"/>: itererer over
/// alle anlegg, kjører <see cref="VaktRoiCalculator"/> per anlegg og aggregerer
/// resultatene. Mønster matcher <see cref="PortfolioQueryService"/>.
///
/// Caching: i v1 kjøres beregningen synkront uten cache. Vakt-ROI er
/// deterministisk for en gitt periode (samme imports + annoteringer gir
/// samme resultat), så Memory-cache med 5 min TTL kan legges til senere
/// hvis ytelse blir et problem.
/// </summary>
public sealed class PortfolioVaktRoiQueryService : IPortfolioVaktRoiQueryService
{
    private readonly KraftverkDbContext _db;
    private readonly INedetidQueryService _nedetid;
    private readonly IOverflowQueryService _overflow;
    private readonly VaktRoiCalculator _calculator;
    private readonly ILogger<PortfolioVaktRoiQueryService> _log;

    public PortfolioVaktRoiQueryService(
        KraftverkDbContext db,
        INedetidQueryService nedetid,
        IOverflowQueryService overflow,
        VaktRoiCalculator calculator,
        ILogger<PortfolioVaktRoiQueryService> log)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _nedetid = nedetid ?? throw new ArgumentNullException(nameof(nedetid));
        _overflow = overflow ?? throw new ArgumentNullException(nameof(overflow));
        _calculator = calculator ?? throw new ArgumentNullException(nameof(calculator));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<PortfolioVaktRoiResponse> GetAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc,
        double kapasitetsfaktor, int topN, CancellationToken ct)
    {
        if (toUtc <= fromUtc)
        {
            return new PortfolioVaktRoiResponse(
                fromUtc, toUtc, kapasitetsfaktor,
                PlantCount: 0, PlantsWithData: 0,
                TotalReddetNok: 0, TotalReddbareEvents: 0, TotalEvents: 0,
                PerPlant: Array.Empty<PortfolioVaktRoiPlantSummary>(),
                TopEvents: Array.Empty<PortfolioVaktRoiTopEvent>(),
                MonthlyTrend: Array.Empty<PortfolioVaktRoiMonthlyPoint>());
        }

        var faktor = Math.Clamp(kapasitetsfaktor, 0.0, 1.0);
        var topNCapped = Math.Clamp(topN, 1, 100);

        var plants = await _db.Plants
            .OrderBy(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var perPlant = new List<PortfolioVaktRoiPlantSummary>();
        var allTopCandidates = new List<PortfolioVaktRoiTopEvent>();
        var monthlyByKey = new Dictionary<(int Year, int Month), (double Nok, int Events)>();
        var plantsWithData = 0;
        var totalReddetNok = 0d;
        var totalReddbare = 0;
        var totalEvents = 0;

        foreach (var plant in plants)
        {
            var events = await _nedetid.ListEventsAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);
            if (events.Count == 0)
            {
                continue;
            }
            plantsWithData++;

            // Snitt-spotpris-proxy fra events (samme metode som per-plant-endepunktet).
            double snittSpot = 500;
            var sumTapMwh = events.Sum(e => e.TapMwh);
            var sumTapNok = events.Sum(e => e.TapNok);
            if (sumTapMwh > 0 && sumTapNok > 0) snittSpot = sumTapNok / sumTapMwh;

            var dataset = await _overflow.GetOverflowDatasetAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);
            var snittUbalansetillegg = await _nedetid.GetAvgImbalancePremiumAsync(plant.Id, fromUtc, toUtc, ct)
                .ConfigureAwait(false);

            var roi = _calculator.Calculate(
                events, plant.InstalledCapacityMw, snittSpot, faktor,
                dataset.OverflowHours, overflowDataAvailable: dataset.DataAvailable,
                snittUbalansetillegg_NokMwh: snittUbalansetillegg);

            var plantReddetNok = roi.Sum(r => r.ReddetNok);
            var plantReddbare = roi.Count(r => r.ErInnenforVakt && r.ErReddbar);

            perPlant.Add(new PortfolioVaktRoiPlantSummary(
                PlantId: plant.Id,
                PlantName: plant.Name,
                InstalledCapacityMw: plant.InstalledCapacityMw,
                ReddetNok: plantReddetNok,
                ReddbareEvents: plantReddbare,
                TotaleEvents: events.Count));

            totalReddetNok += plantReddetNok;
            totalReddbare += plantReddbare;
            totalEvents += events.Count;

            // Top-N-kandidater + månedstrend: kun events som faktisk reddet noe.
            foreach (var r in roi.Where(r => r.ReddetNok > 0))
            {
                allTopCandidates.Add(new PortfolioVaktRoiTopEvent(
                    PlantId: plant.Id,
                    PlantName: plant.Name,
                    StartUtc: r.Event.StartUtc,
                    EndUtc: r.Event.EndUtc,
                    VarighetTimer: r.Event.VarighetTimer,
                    Kategori: r.Event.Category.ToString(),
                    CauseCode: r.Event.CauseCode,
                    ReddetNok: r.ReddetNok,
                    EkstraTimerSpart: r.EkstraTimerSpart));

                var localMonth = r.Event.StartUtc.ToLocalTime();
                var key = (localMonth.Year, localMonth.Month);
                if (monthlyByKey.TryGetValue(key, out var prev))
                {
                    monthlyByKey[key] = (prev.Nok + r.ReddetNok, prev.Events + 1);
                }
                else
                {
                    monthlyByKey[key] = (r.ReddetNok, 1);
                }
            }
        }

        var topEvents = allTopCandidates
            .OrderByDescending(e => e.ReddetNok)
            .Take(topNCapped)
            .ToList();

        var monthlyTrend = monthlyByKey
            .OrderBy(kv => kv.Key.Year).ThenBy(kv => kv.Key.Month)
            .Select(kv => new PortfolioVaktRoiMonthlyPoint(
                Year: kv.Key.Year,
                Month: kv.Key.Month,
                TotalReddetNok: kv.Value.Nok,
                ReddbareEvents: kv.Value.Events))
            .ToList();

        _log.LogInformation(
            "PortfolioVaktRoi: {PlantCount} anlegg, {WithData} med data — total reddet {Total:F0} NOK, {Reddbare}/{Events} events.",
            plants.Count, plantsWithData, totalReddetNok, totalReddbare, totalEvents);

        return new PortfolioVaktRoiResponse(
            FromUtc: fromUtc,
            ToUtc: toUtc,
            Kapasitetsfaktor: faktor,
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
}
