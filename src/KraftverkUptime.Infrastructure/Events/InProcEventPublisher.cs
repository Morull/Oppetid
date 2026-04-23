using System.Collections.Concurrent;
using KraftverkUptime.Core.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Events;

/// <summary>
/// Egenskrevet in-proc dispatcher (~50 linjer) som erstatter MediatR. Publiserer til alle registrerte
/// <see cref="IEventHandler{T}"/>. Feil i én handler tar ikke ned de andre.
///
/// Additivt: v2 kan wrapse denne eller registrere en annen <see cref="IEventPublisher"/> som publiserer
/// både in-proc og til Event Hub.
/// </summary>
public sealed class InProcEventPublisher : IEventPublisher
{
    private static readonly ConcurrentDictionary<Type, Type> HandlerTypeCache = new();

    private readonly IServiceProvider _services;
    private readonly ILogger<InProcEventPublisher> _logger;

    public InProcEventPublisher(IServiceProvider services, ILogger<InProcEventPublisher> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task PublishAsync<T>(T domainEvent, CancellationToken ct = default) where T : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        // Support both static T (compile-time) og runtime-type (when boxed).
        var runtimeType = domainEvent.GetType();
        var handlerType = HandlerTypeCache.GetOrAdd(runtimeType,
            static t => typeof(IEnumerable<>).MakeGenericType(typeof(IEventHandler<>).MakeGenericType(t)));

        using var scope = _services.CreateScope();
        var handlers = (System.Collections.IEnumerable)scope.ServiceProvider.GetRequiredService(handlerType);

        foreach (var handler in handlers)
        {
            try
            {
                var method = handler!.GetType().GetMethod(nameof(IEventHandler<IDomainEvent>.HandleAsync))!;
                var task = (Task)method.Invoke(handler, [domainEvent, ct])!;
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event handler {Handler} failed for event {EventType} ({EventId}).",
                    handler!.GetType().Name, runtimeType.Name, domainEvent.EventId);
            }
        }
    }
}
