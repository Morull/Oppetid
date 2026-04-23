namespace KraftverkUptime.Core.Notifications;

/// <summary>
/// Abstrakt varsling – topic-basert publish-subscribe.
/// V1: skriver til structured log (synlig i App Insights).
/// V2: Azure Communication Services (SMS/e-post), Teams-webhook, egen webhook.
///
/// Topic er dot-separert, f.eks. "uptime.import.failed", "plant.config.changed".
/// Recipients er valgfritt – hvis null bruker implementasjonen topic-basert abonnement.
/// </summary>
public interface INotificationService
{
    Task SendAsync(
        string topic,
        object payload,
        IEnumerable<string>? recipients = null,
        CancellationToken ct = default);
}
