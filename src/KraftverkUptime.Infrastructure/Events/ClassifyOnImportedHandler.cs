using System.Diagnostics;
using KraftverkUptime.Core.Analysis;
using KraftverkUptime.Core.Events;
using KraftverkUptime.Modules.Classification.Dtos;
using KraftverkUptime.Modules.Reporting;
using KraftverkUptime.Modules.Settlement.Jobs;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Events;

/// <summary>
/// Konsumerer <see cref="SettlementImportedEvent"/> og trigger
/// klassifisering umiddelbart etter import.
///
/// Strategi B (on-demand): rapporten persisteres ikke. Formålet her er å
///   1. varme <see cref="IUptimePeriodProvider"/> sin parsed-settlement cache,
///   2. verifisere at klassifiseringen faktisk kjører uten feil,
///   3. logge KPI-sammendrag for observability.
///
/// Ved senere oppgradering til strategi A (persister rapport) utvides denne
/// handleren med et IUptimeReportStore-kall; signaturen mot publisher og
/// eventet selv forblir uendret.
///
/// Handleren er idempotent: cache-hit er ingen-ops, og logging er trygg å
/// gjenta. InProcEventPublisher isolerer exceptions per handler, så en feil
/// her tar ikke ned eventuelle andre SettlementImported-konsumenter.
/// </summary>
public sealed class ClassifyOnImportedHandler : IEventHandler<SettlementImportedEvent>
{
    private readonly IUptimePeriodProvider _periodProvider;
    private readonly IAnalyzer<UptimePeriod, UptimeReport> _analyzer;
    private readonly ILogger<ClassifyOnImportedHandler> _logger;

    public ClassifyOnImportedHandler(
        IUptimePeriodProvider periodProvider,
        IAnalyzer<UptimePeriod, UptimeReport> analyzer,
        ILogger<ClassifyOnImportedHandler> logger)
    {
        _periodProvider = periodProvider ?? throw new ArgumentNullException(nameof(periodProvider));
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
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

        sw.Stop();

        _logger.LogInformation(
            "Klassifisering ferdig for plant {PlantId} på {Ms} ms: {Classified} klassifiserte timer, {Kpis} KPI-er. Event {EventId}.",
            domainEvent.PlantId, sw.ElapsedMilliseconds, report.Classified.Count, report.Kpis.Count, domainEvent.EventId);
    }
}
