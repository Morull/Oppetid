namespace KraftverkUptime.Core.Jobs;

/// <summary>
/// Produsent-side av jobbkøen. En jobb er en vilkårlig DTO; konsumenten velges
/// ved DI-oppslag av <see cref="IJobHandler{TJob}"/> for samme type.
///
/// V1: System.Threading.Channels (in-process, ikke persistent).
/// V2: Azure Service Bus / Storage Queues.
/// </summary>
public interface IJobQueue
{
    Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : notnull;
}
