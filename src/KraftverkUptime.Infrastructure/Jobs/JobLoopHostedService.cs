using KraftverkUptime.Core.Jobs;
using KraftverkUptime.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KraftverkUptime.Infrastructure.Jobs;

/// <summary>
/// HostedService som dequeuer fra ChannelsJobQueue og dispatcher til riktig IJobHandler via DI.
/// Kjøres i Worker-prosessen. Registrerbar også i API hvis du vil dele prosess i dev.
/// </summary>
public sealed class JobLoopHostedService : BackgroundService
{
    private readonly ChannelsJobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobLoopHostedService> _logger;
    private readonly int _concurrency;
    private readonly int _retryBackoffMs;

    public JobLoopHostedService(
        ChannelsJobQueue queue,
        IServiceScopeFactory scopeFactory,
        IOptions<JobQueueOptions> options,
        ILogger<JobLoopHostedService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _concurrency = options.Value.WorkerConcurrency;
        _retryBackoffMs = options.Value.RetryBackoffMs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobLoopHostedService started with concurrency {Concurrency}.", _concurrency);

        var workers = Enumerable.Range(0, _concurrency)
            .Select(i => Task.Run(() => WorkerLoopAsync(i, stoppingToken), stoppingToken))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task WorkerLoopAsync(int workerIndex, CancellationToken ct)
    {
        await foreach (var envelope in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var handlerType = typeof(IJobHandler<>).MakeGenericType(envelope.JobType);
                var handler = scope.ServiceProvider.GetService(handlerType);

                if (handler is null)
                {
                    _logger.LogError("No IJobHandler<{JobType}> registered. Job dropped.", envelope.JobType.FullName);
                    continue;
                }

                var method = handlerType.GetMethod(nameof(IJobHandler<object>.HandleAsync))!;
                var task = (Task)method.Invoke(handler, [envelope.Job, ct])!;
                await task.ConfigureAwait(false);

                _logger.LogDebug("Worker {Index} completed job {JobType} (corr {Correlation})",
                    workerIndex, envelope.JobType.Name, envelope.CorrelationId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job {JobType} failed. Worker {Index} backing off {Backoff} ms.",
                    envelope.JobType.Name, workerIndex, _retryBackoffMs);
                try
                {
                    await Task.Delay(_retryBackoffMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("Worker {Index} exiting.", workerIndex);
    }
}
