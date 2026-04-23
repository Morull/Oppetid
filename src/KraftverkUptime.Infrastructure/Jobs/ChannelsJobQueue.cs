using System.Threading.Channels;
using KraftverkUptime.Core.Jobs;
using KraftverkUptime.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Infrastructure.Jobs;

/// <summary>
/// V1-implementasjon av IJobQueue. Én in-memory kø for hele prosessen.
/// Worker-en (samme prosess) leser fra samme instans via JobLoopHostedService.
/// V2 erstattes med Azure Service Bus – samme interface, annen implementasjon.
/// </summary>
public sealed class ChannelsJobQueue : IJobQueue
{
    private readonly Channel<JobEnvelope> _channel;

    public ChannelsJobQueue(IOptions<JobQueueOptions> options)
    {
        var opt = options.Value;
        _channel = Channel.CreateBounded<JobEnvelope>(new BoundedChannelOptions(opt.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ChannelReader<JobEnvelope> Reader => _channel.Reader;

    public async Task EnqueueAsync<TJob>(TJob job, CancellationToken ct = default) where TJob : notnull
    {
        var envelope = new JobEnvelope(typeof(TJob), job, DateTimeOffset.UtcNow,
            System.Diagnostics.Activity.Current?.TraceId.ToString());
        await _channel.Writer.WriteAsync(envelope, ct).ConfigureAwait(false);
    }
}

/// <summary>Intern konvolutt for jobber i kanalkøen. Inneholder type og korrelasjons-ID.</summary>
public sealed record JobEnvelope(Type JobType, object Job, DateTimeOffset EnqueuedAt, string? CorrelationId);
