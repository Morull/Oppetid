namespace KraftverkUptime.Core.Events;

/// <summary>
/// Eneste event-fasade modulkode refererer – justering (g) fra Prompt 1 v1.
/// Modulkode bruker ALDRI en konkret dispatcher (MediatR eller egenskrevet)
/// direkte. Dette gir additiv oppgradering: V2 publiserer i tillegg til Event Hub
/// via samme fasade, uten å berøre modulkode.
///
/// V1: InProcEventPublisher ruter til IEnumerable&lt;IEventHandler&lt;T&gt;&gt;.
/// V2: Event Hub fan-out legges til som decorator eller tilleggspublisher.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync<T>(T domainEvent, CancellationToken ct = default) where T : IDomainEvent;
}
