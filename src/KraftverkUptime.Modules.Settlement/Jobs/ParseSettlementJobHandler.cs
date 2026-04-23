using KraftverkUptime.Core.Events;
using KraftverkUptime.Core.Jobs;
using KraftverkUptime.Core.Security;
using KraftverkUptime.Core.Storage;
using KraftverkUptime.Modules.Settlement.Persistence;
using KraftverkUptime.Modules.Settlement.Quality;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Modules.Settlement.Jobs;

/// <summary>
/// Konsument for <see cref="ParseSettlementJob"/>.
/// Flyt:
///   1. Hent Excel-blob fra <see cref="IFileStorage"/>.
///   2. Parse til <c>ParsedSettlement</c>.
///   3. Bygg DataQualityReport + beriket hourly.
///   4. Persister metadata via <see cref="ISettlementImportRecorder"/>.
///   5. Logg til AuditLogger.
///   6. Publiser <see cref="SettlementImportedEvent"/>.
///
/// Handleren er idempotent: samme IdempotencyKey upserter samme rad i
/// settlement_imports (via unik constraint).
/// </summary>
public sealed class ParseSettlementJobHandler : IJobHandler<ParseSettlementJob>
{
    private readonly ISettlementParser _parser;
    private readonly DataQualityReportBuilder _qualityBuilder;
    private readonly IFileStorage _fileStorage;
    private readonly ISettlementImportRecorder _importRecorder;
    private readonly IEventPublisher _events;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ParseSettlementJobHandler> _logger;

    public ParseSettlementJobHandler(
        ISettlementParser parser,
        DataQualityReportBuilder qualityBuilder,
        IFileStorage fileStorage,
        ISettlementImportRecorder importRecorder,
        IEventPublisher events,
        IAuditLogger audit,
        ILogger<ParseSettlementJobHandler> logger)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _qualityBuilder = qualityBuilder ?? throw new ArgumentNullException(nameof(qualityBuilder));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _importRecorder = importRecorder ?? throw new ArgumentNullException(nameof(importRecorder));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task HandleAsync(ParseSettlementJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        _logger.LogInformation(
            "Starter settlement-import for plant {PlantId}, blob {Blob}, idempotency {Idem}",
            job.PlantId, job.BlobPath, job.IdempotencyKey);

        await using var stream = await _fileStorage.GetAsync(job.BlobPath, ct).ConfigureAwait(false);
        var parsed = await _parser.ParseAsync(stream, ct).ConfigureAwait(false);

        var (quality, _) = _qualityBuilder.Build(parsed);

        await _importRecorder.RecordAsync(new SettlementImportRecord
        {
            OwnerOrgId = job.OwnerOrgId,
            PlantId = job.PlantId,
            IdempotencyKey = job.IdempotencyKey,
            BlobPath = job.BlobPath,
            PlantName = parsed.PlantName,
            SchemaVersion = parsed.SchemaVersion,
            PeriodStartUtc = parsed.PeriodStartUtc,
            PeriodEndUtc = parsed.PeriodEndUtc,
            HourCount = parsed.Hourly.Count,
            IssueCount = parsed.Issues.Count,
            ImportedAtUtc = DateTimeOffset.UtcNow,
            CorrelationId = job.CorrelationId,
        }, ct).ConfigureAwait(false);

        await _audit.LogAsync(
            action: "settlement.imported",
            entityType: "SettlementImport",
            entityId: job.IdempotencyKey,
            payload: new
            {
                job.PlantId,
                parsed.PlantName,
                parsed.SchemaVersion,
                HourCount = parsed.Hourly.Count,
                IssueCount = parsed.Issues.Count,
                quality.HoursAccepted,
                quality.HoursFlagged,
                quality.HoursRejected,
            },
            ct).ConfigureAwait(false);

        await _events.PublishAsync(new SettlementImportedEvent
        {
            PlantId = job.PlantId,
            OwnerOrgId = job.OwnerOrgId,
            BlobPath = job.BlobPath,
            IdempotencyKey = job.IdempotencyKey,
            PeriodStartUtc = parsed.PeriodStartUtc,
            PeriodEndUtc = parsed.PeriodEndUtc,
            HourCount = parsed.Hourly.Count,
            IssueCount = parsed.Issues.Count,
            CorrelationId = job.CorrelationId,
        }, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Settlement-import fullført: {Hours} timer, {Issues} avvik",
            parsed.Hourly.Count, parsed.Issues.Count);
    }
}
