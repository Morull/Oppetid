namespace KraftverkUptime.Core.Analysis;

/// <summary>
/// Genereisk analyse-kontrakt: mottar én input-artefakt, produserer én output.
/// Implementasjoner skal være stateless og idempotente.
/// Inndata skal ikke muteres; utdata skal være en ny, uavhengig instans.
/// </summary>
/// <typeparam name="TInput">Analyseinndata (f.eks. SettlementPeriod).</typeparam>
/// <typeparam name="TOutput">Analyseutdata (f.eks. UptimeReport).</typeparam>
public interface IAnalyzer<in TInput, TOutput>
{
    Task<TOutput> AnalyzeAsync(TInput input, CancellationToken ct);
}
