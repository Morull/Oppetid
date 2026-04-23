namespace KraftverkUptime.Core.Events;

/// <summary>
/// Versjonert domenehendelse. SchemaVersion inkrementeres ved brytende
/// endringer i event-payload; konsumenter skal kunne håndtere flere versjoner
/// eller utføre opcast.
/// </summary>
public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
    string? CorrelationId { get; }
    int SchemaVersion { get; }
}
