using KraftverkUptime.Core.Events;

namespace KraftverkUptime.Modules.Settlement.Jobs;

/// <summary>
/// Jobb-DTO for import av portaleksport. Legges på <c>IJobQueue</c> av
/// API-endepunktet når bruker laster opp en fil, og kjøres av
/// <see cref="ParseSettlementJobHandler"/> i Worker-prosessen.
///
/// <see cref="BlobPath"/> er stien i <c>IFileStorage</c> der opplastet Excel er lagret.
/// <see cref="IdempotencyKey"/> hindrer duplikat-import (SHA256 av fil + plantId + periode).
/// </summary>
public sealed record ParseSettlementJob(
    string PlantId,
    string OwnerOrgId,
    string BlobPath,
    string IdempotencyKey,
    string? CorrelationId = null);

/// <summary>
/// Publiseres når en importjobb er fullført.
/// Konsumeres av UptimeAnalyzer.Settlement-modulen for å trigge klassifisering.
///
/// <see cref="PeriodStartUtc"/> og <see cref="PeriodEndUtc"/> reflekterer
/// tidsspennet i den importerte filen og lar konsumenter slå opp riktig
/// <c>UptimePeriod</c> uten et ekstra DB-kall.
/// </summary>
public sealed record SettlementImportedEvent : IDomainEvent
{
    public required string PlantId { get; init; }
    public required string OwnerOrgId { get; init; }
    public required string BlobPath { get; init; }
    public required DateTimeOffset PeriodStartUtc { get; init; }
    public required DateTimeOffset PeriodEndUtc { get; init; }
    public required int HourCount { get; init; }
    public required int IssueCount { get; init; }

    // IDomainEvent
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; init; }
    public int SchemaVersion => 2;
}
