namespace KraftverkUptime.Modules.Settlement.Persistence;

/// <summary>
/// Persistert metadata om en gjennomført import. Selve tidsseriedataene ligger
/// i blobben det pekes på via <see cref="BlobPath"/>; denne raden lar systemet
/// finne riktig import for et gitt anlegg og periode uten å skanne lagring.
///
/// Unik per (OwnerOrgId, PlantId, IdempotencyKey) – samme fil lastet opp to
/// ganger skal gi én rad.
/// </summary>
public sealed record SettlementImportRecord
{
    public required string OwnerOrgId { get; init; }
    public required string PlantId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string BlobPath { get; init; }
    public required string PlantName { get; init; }
    public required string SchemaVersion { get; init; }
    public required DateTimeOffset PeriodStartUtc { get; init; }
    public required DateTimeOffset PeriodEndUtc { get; init; }
    public required int HourCount { get; init; }
    public required int IssueCount { get; init; }
    public required DateTimeOffset ImportedAtUtc { get; init; }
    public string? CorrelationId { get; init; }
}
