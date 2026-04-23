using FluentAssertions;
using KraftverkUptime.Core.Events;
using KraftverkUptime.Infrastructure.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KraftverkUptime.Infrastructure.Tests;

public class InProcEventPublisherTests
{
    private sealed record TestEvent(string Message)
        : IDomainEvent
    {
        public Guid EventId { get; init; } = Guid.NewGuid();
        public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
        public string? CorrelationId { get; init; }
        public int SchemaVersion { get; init; } = 1;
    }

    private sealed class TestHandler : IEventHandler<TestEvent>
    {
        public List<string> Received { get; } = new();
        public Task HandleAsync(TestEvent domainEvent, CancellationToken ct)
        {
            Received.Add(domainEvent.Message);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHandler : IEventHandler<TestEvent>
    {
        public Task HandleAsync(TestEvent domainEvent, CancellationToken ct)
            => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Dispatches_To_All_Registered_Handlers()
    {
        var handler1 = new TestHandler();
        var handler2 = new TestHandler();

        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<TestEvent>>(handler1);
        services.AddSingleton<IEventHandler<TestEvent>>(handler2);

        var provider = services.BuildServiceProvider();
        var publisher = new InProcEventPublisher(provider, NullLogger<InProcEventPublisher>.Instance);

        await publisher.PublishAsync(new TestEvent("hello"));

        handler1.Received.Should().ContainSingle().Which.Should().Be("hello");
        handler2.Received.Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public async Task Handler_Failure_Does_Not_Stop_Other_Handlers()
    {
        var good = new TestHandler();

        var services = new ServiceCollection();
        services.AddSingleton<IEventHandler<TestEvent>>(new ThrowingHandler());
        services.AddSingleton<IEventHandler<TestEvent>>(good);

        var provider = services.BuildServiceProvider();
        var publisher = new InProcEventPublisher(provider, NullLogger<InProcEventPublisher>.Instance);

        await publisher.PublishAsync(new TestEvent("survive"));
        good.Received.Should().ContainSingle().Which.Should().Be("survive");
    }
}
