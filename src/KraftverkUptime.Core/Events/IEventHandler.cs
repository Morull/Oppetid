namespace KraftverkUptime.Core.Events;

/// <summary>
/// Konsument for én domenehendelse. Flere handlere per hendelse er tillatt.
/// Handlere skal være idempotente – samme event kan leveres flere ganger.
///
/// In-proc dispatcher i Infrastructure oppdager alle registrerte handlere
/// via DI og invokerer dem parallelt med isolert exception-håndtering
/// (én handler-feil skal ikke stoppe de andre).
/// </summary>
public interface IEventHandler<in T> where T : IDomainEvent
{
    Task HandleAsync(T domainEvent, CancellationToken ct);
}
