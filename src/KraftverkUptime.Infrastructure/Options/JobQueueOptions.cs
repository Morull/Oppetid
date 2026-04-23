using System.ComponentModel.DataAnnotations;

namespace KraftverkUptime.Infrastructure.Options;

public sealed class JobQueueOptions
{
    public const string SectionName = "JobQueue";

    [Range(1, 10_000)]
    public int Capacity { get; set; } = 1024;

    [Range(1, 64)]
    public int WorkerConcurrency { get; set; } = 2;

    /// <summary>Backoff før job retries. Millisekunder.</summary>
    [Range(0, 600_000)]
    public int RetryBackoffMs { get; set; } = 5_000;
}
