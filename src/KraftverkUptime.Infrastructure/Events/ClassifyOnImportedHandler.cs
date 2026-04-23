using System.Diagnostics;
using KraftverkUptime.Core.Analysis;
using KraftverkUptime.Core.Events;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting;
using KraftverkUptime.Modules.Reporting.Storage;
using KraftverkUptime.Modules.Settlement.Jobs;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Events;

/// <summary>
/// Konsumerer <see cref="SettlementImportedEvent"/> og trigger
/// klassifisering umiddelbart etter import.
///
/// Strategi A (persister rapport): etter klassifisering lagres
/// <see cref="UptimeReport"/> via <see cref="IUptimeReportStore"/> slik at
/// GET-endepunktet kan hente den uten å kjøre klassifisering på nytt.
/// Cache-warming og observability-logging beholdes fra forrige iterasjon.
///
/// Handleren er idempotent: cache-hit er ingen-ops, lagring bruker upsert-
/// semantikk, og logging er trygg å gjenta. InProcEventPublisher isolerer
/// exceptions per handler, så en feil her tar ikke ned eventuelle andre
/// SettlementImported-konsumenter.
/// </summary>
public sealed class ClassifyOnImportedHandler : IEventHandler<SettlementImportedEvent>
{
    private readonly IUptimePeriodProvider _periodProvider;
    private readonly IAnalyzer<UptimePeriod, UptimeReport> _analyzer;
    private readonly IUptimeReportStore _reportStore;
    private readonly ILogger<ClassifyOnImportedHandler> _logger;

    public ClassifyOnImportedHandler(
        IUptimePeriodProvider periodProvider,
        IAnalyzer<UptimePeriod, UptimeReport> analyzer,
        IUptimeReportStore reportStore,
        ILogger<ClassifyOnImportedHandler> logger)
    {
        _periodProvider = periodProvider ?? throw new ArgumentNullException(nameof(periodProvider));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _reportStore = reportStore ?? throw new ArgumentNullException(nameof(reportStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task HandleAsync(SettlementImportedEvent domainEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "SettlementImported mottatt: plant {PlantId}, periode {Start:o}–{End:o}, {Hours} timer, {Issues} avvik. Kjører klassifisering.",
            domainEvent.PlantId, domainEvent.PeriodStartUtc, domainEvent.PeriodEndUtc,
            domainEvent.HourCount, domainEvent.IssueCount);

        var period = await _periodProvider
            .GetAsync(domainEvent.PlantId, domainEvent.PeriodStartUtc, domainEvent.PeriodEndUtc, ct)
            .ConfigureAwait(false);

        var report = await _analyzer.AnalyzeAsync(period, ct).ConfigureAwait(false);

        await _reportStore.SaveAsync(
            domainEvent.OwnerOrgId,
            domainEvent.PlantId,
            domainEvent.IdempotencyKey,
            report,
            ct).ConfigureAwait(false);

        sw.Stop();

        _logger.LogInformation(
            "Klassifisering ferdig for plant {PlantId} på {Ms} ms: {Classified} klassifiserte timer, {Kpis} KPI-er. Event {EventId}. Rapport lagret.",
            domainEvent.PlantId, sw.ElapsedMilliseconds, report.Classified.Count, report.Kpis.Count, domainEvent.EventId);
    }
}
