using Agw.Shared.EventBus.Abstractions;

using Microsoft.Extensions.DependencyInjection;

namespace Agw.Shared.Tests.DomainEvents;

public class InMemoryDomainEventBusTests
{
    [Fact]
    public async Task PublishAsync_WhenHandlersRegistered_InvokesHandlersInRegistrationOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        var calls = new List<string>();

        services.AddSingleton(calls);
        services.AddSingleton<IDomainEventHandler<TestDomainEvent>, FirstTestDomainEventHandler>();
        services.AddSingleton<IDomainEventHandler<TestDomainEvent>, SecondTestDomainEventHandler>();
        services.AddSingleton<IDomainEventBus, InMemoryDomainEventBus>();

        await using var serviceProvider = services.BuildServiceProvider();
        var eventBus = serviceProvider.GetRequiredService<IDomainEventBus>();

        await eventBus.PublishAsync(new TestDomainEvent("created"), cancellationToken);

        Assert.Equal(["first:created", "second:created"], calls);
    }

    private sealed record TestDomainEvent(string Name) : IDomainEvent;

    private sealed class FirstTestDomainEventHandler(List<string> calls) : IDomainEventHandler<TestDomainEvent>
    {
        public Task HandleAsync(TestDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            calls.Add("first:" + domainEvent.Name);
            return Task.CompletedTask;
        }
    }

    private sealed class SecondTestDomainEventHandler(List<string> calls) : IDomainEventHandler<TestDomainEvent>
    {
        public Task HandleAsync(TestDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            calls.Add("second:" + domainEvent.Name);
            return Task.CompletedTask;
        }
    }
}
