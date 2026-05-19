using KraftverkUptime.Core.Domain;
using KraftverkUptime.Modules.Annotations.Overlay;
using KraftverkUptime.Modules.Annotations.Repositories;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Scada.Repositories;
using KraftverkUptime.Modules.Settlement.Persistence;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Modules.Reporting.Nedetid;

/// <summary>
/// Standard-implementasjonen av <see cref="INedetidQueryService"/>:
///   1. Lister alle settlement-imports for plantet som overlapper [from, to)
///   2. Henter UptimeReport for hver import (med klassifiserte timer)
///   3. <b>Annotation overlay</b>: lar manuelle annoteringer overstyre
///      klassifikator-output (samme tjeneste som ReportDetail bruker via
///      <c>GET /report</c>). Krevd 2026-05-05 for å sikre at en redigering
///      i Rapport reflekteres umiddelbart i Nedetid og Vakt-ROI.
///   4. Slår sammen alle Classified-radene til én sortert liste
///   5. Henter operlog-events fra <see cref="IClassifiedEventRepository"/>
///   6. Aggregerer via <see cref="DowntimeEventAggregator"/>
///   7. Filtrerer events som ligger helt utenfor [from, to)
/// </summary>
public sealed class NedetidQueryService : INedetidQueryService
{
    private readonly ISettlementImportRecorder _imports;
    private readonly IUptimeReportStore _reports;
    private readonly IClassifiedEventRepository _operlog;
    private readonly AnnotationOverlayService _annotationOverlay;
    private readonly IDowntimeAnnotationRepository _annotationRepo;
    private readonly IDowntimeCategoryRepository _categoryRepo;
    private readonly ILogger<NedetidQueryService> _log;

    public NedetidQueryService(
        ISettlementImportRecorder imports,
        IUptimeReportStore reports,
        IClassifiedEventRepository operlog,
        AnnotationOverlayService annotationOverlay,
        IDowntimeAnnotationRepository annotationRepo,
        IDowntimeCategoryRepository categoryRepo,
        ILogger<NedetidQueryService> log)
    {
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _operlog = operlog ?? throw new ArgumentNullException(nameof(operlog));
        _annotationOverlay = annotationOverlay ?? throw new ArgumentNullException(nameof(annotationOverlay));
        _annotationRepo = annotationRepo ?? throw new ArgumentNullException(nameof(annotationRepo));
        _categoryRepo = categoryRepo ?? throw new ArgumentNullException(nameof(categoryRepo));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<IReadOnlyList<DowntimeEvent>> ListEventsAsync(
        string plantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        if (toUtc <= fromUtc)
        {
            return Array.Empty<DowntimeEvent>();
        }

        var imports = await _imports
            .ListForPlantAsync(plantId, fromUtc, toUtc, limit: 500, ct)
            .ConfigureAwait(false);

        if (imports.Count == 0)
        {
            _log.LogInformation("NedetidQuery: ingen imports for {PlantId} i [{From}, {To})",
                plantId, fromUtc, toUtc);
            return Array.Empty<DowntimeEvent>();
        }

        // Slå sammen klassifiserte timer fra alle overlappende imports.
        // Dedupliser på TimeUtc — hvis samme time finnes i flere imports
        // (re-import-scenario), bruk den nyeste.
        var hoursByTime = new Dictionary<DateTimeOffset, ClassifiedHourlyRow>();
        foreach (var imp in imports.OrderBy(i => i.ImportedAtUtc))
        {
            var report = await _reports
                .GetAsync(imp.OwnerOrgId, imp.PlantId, imp.IdempotencyKey, ct)
                .ConfigureAwait(false);
            if (report is null) continue;

            // Anvend annotation overlay før vi plukker ut Classified-radene.
            // Annotering kan flytte event mellom kategorier (eks. "TripFeil"
            // → "PlanlagtVedlikehold") og dermed påvirke nedetid + Vakt-ROI.
            var overlaid = await _annotationOverlay
                .ApplyAsync(report, _annotationRepo, _categoryRepo, ct)
                .ConfigureAwait(false);

            foreach (var h in overlaid.Classified)
            {
                if (h.TimeUtc < fromUtc || h.TimeUtc >= toUtc) continue;
                hoursByTime[h.TimeUtc] = h; // siste import vinner
            }
        }

        if (hoursByTime.Count == 0)
        {
            return Array.Empty<DowntimeEvent>();
        }

        var sortedHours = hoursByTime.Values
            .OrderBy(h => h.TimeUtc)
            .ToList();

        // Operlog-events for samme periode. Tomt resultat hvis ingen operlog er importert.
        var operlogEvents = await _operlog
            .ListAsync(plantId, fromUtc, toUtc, ct)
            .ConfigureAwait(false);

        var events = DowntimeEventAggregator.Aggregate(plantId, sortedHours, operlogEvents);

        _log.LogInformation(
            "NedetidQuery: {EventCount} events for {PlantId} fra {HourCount} timer (operlog: {OperlogCount})",
            events.Count, plantId, sortedHours.Count, operlogEvents.Count);

        return events;
    }

    public async Task<PlanByHourResult> GetProduksjonplanByHourAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        if (toUtc <= fromUtc)
        {
            return new PlanByHourResult(
                new Dictionary<DateTimeOffset, double>(),
                new HashSet<DateTimeOffset>());
        }

        // Hent imports som dekker både settlement-perioden OG bakover (4 uker
        // for proxy) + fremover (3 dager for counterfactual_end som maks går
        // fra fredag 23:59 til mandag 08:00 lokal).
        var fetchFrom = fromUtc.AddDays(-28);
        var fetchTo = toUtc.AddDays(3);
        var imports = await _imports
            .ListForPlantAsync(plantId, fetchFrom, fetchTo, limit: 1000, ct)
            .ConfigureAwait(false);
        if (imports.Count == 0)
        {
            return new PlanByHourResult(
                new Dictionary<DateTimeOffset, double>(),
                new HashSet<DateTimeOffset>());
        }

        // Bygg plan-by-time fra blob-data. Siste import vinner ved overlapp
        // (samme som ListEventsAsync). Annotering påvirker ikke plan-kolonnen,
        // så vi hopper over overlay her.
        var planByTime = new Dictionary<DateTimeOffset, double>();
        foreach (var imp in imports.OrderBy(i => i.ImportedAtUtc))
        {
            var report = await _reports
                .GetAsync(imp.OwnerOrgId, imp.PlantId, imp.IdempotencyKey, ct)
                .ConfigureAwait(false);
            if (report is null) continue;
            foreach (var h in report.Classified)
            {
                if (h.TimeUtc < fetchFrom || h.TimeUtc >= fetchTo) continue;
                // ProduksjonplanMwh er nullable i row-modellen — behandle null som 0
                // siden plan-feltet alltid har en verdi i Hydrogrid-eksport, og null
                // betyr at importeren ikke kunne tolke tallet (sjeldent).
                planByTime[h.TimeUtc] = h.Row.ProduksjonplanMwh ?? 0;
            }
        }

        // Iterer time-for-time i [fromUtc, toUtc + 3d) og fyll inn proxy der
        // direkte data mangler. Vi inkluderer noen ekstra dager etter toUtc
        // slik at Vakt-ROI kan summere plan over counterfactual_end > toUtc.
        var result = new Dictionary<DateTimeOffset, double>();
        var proxyHours = new HashSet<DateTimeOffset>();
        var rangeStart = FloorToHour(fromUtc);
        var rangeEnd = FloorToHour(toUtc.AddDays(3));
        for (var h = rangeStart; h < rangeEnd; h = h.AddHours(1))
        {
            if (planByTime.TryGetValue(h, out var direct))
            {
                result[h] = direct;
                continue;
            }
            // Proxy: samme ukedag/time bakover (1, 2, 3, 4 uker)
            for (var w = 1; w <= 4; w++)
            {
                var proxyTime = h.AddDays(-7 * w);
                if (planByTime.TryGetValue(proxyTime, out var proxyVal))
                {
                    result[h] = proxyVal;
                    proxyHours.Add(h);
                    break;
                }
            }
            // Hvis ingen proxy funnet: hopp over. Calculator treats as 0 for den timen.
        }

        _log.LogDebug(
            "ProduksjonplanByHour for {PlantId} [{From},{To}): {Direct} direkte timer, {Proxy} proxy-timer.",
            plantId, fromUtc, toUtc, result.Count - proxyHours.Count, proxyHours.Count);

        return new PlanByHourResult(result, proxyHours);
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }

    public async Task<double> GetAvgImbalancePremiumAsync(
        string plantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plantId);
        if (toUtc <= fromUtc) return 0;

        // Gjenbruker imports + report-store. Trekker klassifiserte rader fra
        // de samme report-blobbene som ListEventsAsync — DB- og blob-trafikken
        // dupliseres dessverre, men v3 introduserer ikke en ny modell akkurat nå.
        // RK-spread er ikke påvirket av annoteringer (avhenger av spotpris/RK-pris,
        // ikke UnitState/CauseCode), så vi hopper over overlay her.
        var imports = await _imports
            .ListForPlantAsync(plantId, fromUtc, toUtc, limit: 500, ct)
            .ConfigureAwait(false);
        if (imports.Count == 0) return 0;

        double sumDiff = 0;
        var count = 0;

        foreach (var imp in imports.OrderBy(i => i.ImportedAtUtc))
        {
            var report = await _reports
                .GetAsync(imp.OwnerOrgId, imp.PlantId, imp.IdempotencyKey, ct)
                .ConfigureAwait(false);
            if (report is null) continue;

            foreach (var h in report.Classified)
            {
                if (h.TimeUtc < fromUtc || h.TimeUtc >= toUtc) continue;
                var spot = h.Row.SpotprisNokMwh;
                var rk = h.Row.RkPrisNokMwh;
                if (!spot.HasValue || !rk.HasValue) continue;

                var diff = rk.Value - spot.Value;
                if (diff <= 0) continue; // bare timer der RK > spot teller (oppregulering)

                sumDiff += diff;
                count++;
            }
        }

        return count == 0 ? 0 : sumDiff / count;
    }
}
