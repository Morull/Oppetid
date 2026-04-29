using KraftverkUptime.Core.Domain;
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
///   3. Slår sammen alle Classified-radene til én sortert liste
///   4. Henter operlog-events fra <see cref="IClassifiedEventRepository"/>
///   5. Aggregerer via <see cref="DowntimeEventAggregator"/>
///   6. Filtrerer events som ligger helt utenfor [from, to)
///
/// Tar avhengighet av Settlement- og Scada-modulenes repository-kontrakter,
/// men ikke av deres EF-implementasjoner. Det holder modulen lett-testbar.
/// </summary>
public sealed class NedetidQueryService : INedetidQueryService
{
    private readonly ISettlementImportRecorder _imports;
    private readonly IUptimeReportStore _reports;
    private readonly IClassifiedEventRepository _operlog;
    private readonly ILogger<NedetidQueryService> _log;

    public NedetidQueryService(
        ISettlementImportRecorder imports,
        IUptimeReportStore reports,
        IClassifiedEventRepository operlog,
        ILogger<NedetidQueryService> log)
    {
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _operlog = operlog ?? throw new ArgumentNullException(nameof(operlog));
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

            foreach (var h in report.Classified)
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
}
