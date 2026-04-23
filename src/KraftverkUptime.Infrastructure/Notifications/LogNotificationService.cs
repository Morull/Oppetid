using System.Text.Json;
using KraftverkUptime.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace KraftverkUptime.Infrastructure.Notifications;

/// <summary>
/// V1. Skriver varsler til structured log. V2 erstattes av Teams/e-post/webhook-implementasjon.
/// </summary>
public sealed class LogNotificationService : INotificationService
{
    private readonly ILogger<LogNotificationService> _logger;

    public LogNotificationService(ILogger<LogNotificationService> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string topic, object payload, IEnumerable<string>? recipients = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Notification topic={Topic} recipients={Recipients} payload={Payload}",
            topic,
            recipients is null ? "(none)" : string.Join(",", recipients),
            JsonSerializer.Serialize(payload));
        return Task.CompletedTask;
    }
}
