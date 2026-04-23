namespace KraftverkUptime.Core.Reporting;

/// <summary>
/// Bygger en abstrakt rapportmodell fra en forespørsel.
/// Rapportmodellen er format-agnostisk – faktisk rendering til .xlsx/.pdf
/// skjer i <see cref="IReportRenderer"/>.
/// </summary>
/// <typeparam name="TReport">Rapportmodelltype (f.eks. UptimeReport).</typeparam>
public interface IReportBuilder<TReport>
{
    Task<TReport> BuildAsync(ReportRequest request, CancellationToken ct);
}
