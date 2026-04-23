namespace KraftverkUptime.Core.Jobs;

/// <summary>
/// Konsument-side av jobbkøen. Justering (c) fra Prompt 1 v1: lagt til – uten dette
/// hadde Worker vært uten kjørbar kontrakt.
///
/// Én handler per jobb-type. Handler skal være idempotent – samme jobb kan bli
/// levert flere ganger ved feil/retry.
/// </summary>
public interface IJobHandler<in TJob> where TJob : notnull
{
    Task HandleAsync(TJob job, CancellationToken ct);
}
