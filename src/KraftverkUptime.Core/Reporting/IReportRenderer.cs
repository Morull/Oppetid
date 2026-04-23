namespace KraftverkUptime.Core.Reporting;

/// <summary>
/// Renderer en rapportmodell til et konkret utdataformat.
/// Én implementasjon per format – flere renderers kan registreres per TReport
/// og velges ut fra <see cref="Format"/>.
/// </summary>
public interface IReportRenderer
{
    /// <summary>
    /// Filformat som denne rendereren produserer (lowercase, uten punktum):
    /// "xlsx", "pdf", "html", "csv".
    /// </summary>
    string Format { get; }

    /// <summary>
    /// Rendrer rapportmodellen til en strøm.
    /// Strømmen posisjon skal være 0 ved retur. Caller eier strømmen.
    /// </summary>
    Task<Stream> RenderAsync(object report, CancellationToken ct);
}
