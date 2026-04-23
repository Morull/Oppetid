namespace KraftverkUptime.Core.Reporting;

/// <summary>
/// Publiserer en rendret rapport til ett eller flere destinasjoner
/// (Blob Storage, e-post, SharePoint, webhook).
/// Implementasjoner skal være idempotente basert på <c>reportId</c>.
/// </summary>
public interface IReportSink
{
    Task PublishAsync(string reportId, Stream content, string format, CancellationToken ct);
}
