using KraftverkUptime.Core.Domain;

namespace KraftverkUptime.Infrastructure.Persistence.Entities;

/// <summary>
/// EF-entitet for <c>core.settlement_imports</c>. Persistert metadata om en
/// gjennomført settlement-import; selve tidsseriedata ligger i blobben som
/// <see cref="BlobPath"/> peker på. <see cref="Id"/> er stabilt og kan
/// refereres fra audit-logger og domene-events.
///
/// Soft-delete gir retensjonspolicy per org: slettede importer forblir
/// radiologisk tilstede for audit-spor til purge-jobben rydder.
/// </summary>
public sealed class SettlementImport : IOwnedEntity, ISoftDeletable
{
    public long Id { get; set; }

    // IOwnedEntity
    public string OwnerOrgId { get; set; } = string.Empty;
    public string? PlantId { get; set; }

    /// <summary>SHA256 av (fil + plantId + periode) fra ParseSettlementJob.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>Sti i IFileStorage der Excel-en er lagret.</summary>
    public string BlobPath { get; set; } = string.Empty;

    public string PlantName { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;

    public DateTimeOffset PeriodStartUtc { get; set; }
    public DateTimeOffset PeriodEndUtc { get; set; }
    public int HourCount { get; set; }
    public int IssueCount { get; set; }

    public DateTimeOffset ImportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; set; }

    // ISoftDeletable
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
}
