using FluentAssertions;
using KraftverkUptime.Infrastructure.Jobs;
using KraftverkUptime.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

public class ChannelsJobQueueTests
{
    [Fact]
    public async Task Enqueued_Jobs_Are_Readable_In_Order()
    {
        var queue = new ChannelsJobQueue(Microsoft.Extensions.Options.Options.Create(new JobQueueOptions { Capacity = 10 }));

        await queue.EnqueueAsync(new FirstJob("a"));
        await queue.EnqueueAsync(new FirstJob("b"));

        var e1 = await queue.Reader.ReadAsync();
        var e2 = await queue.Reader.ReadAsync();

        e1.JobType.Should().Be<FirstJob>();
        ((FirstJob)e1.Job).Payload.Should().Be("a");
        ((FirstJob)e2.Job).Payload.Should().Be("b");
    }

    private sealed record FirstJob(string Payload);
}
