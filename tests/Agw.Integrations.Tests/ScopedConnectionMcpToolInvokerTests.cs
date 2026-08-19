using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Integrations.Tests;

public class ScopedConnectionMcpToolInvokerTests
{
    [Fact]
    public async Task InvokeAsync_Twice_CreatesAndDisposesFreshSessionScopeForEachInvocation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var recorder = new SessionRecorder();
        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddScoped<IConnectionMcpInvocationSession, TrackingInvocationSession>();
        await using var provider = services.BuildServiceProvider(validateScopes: true);
        var invoker = new ScopedConnectionMcpToolInvoker(provider.GetRequiredService<IServiceScopeFactory>());

        var first = await invoker.InvokeAsync(
            Guid.CreateVersion7(),
            "source",
            "operation",
            new AIFunctionArguments(),
            cancellationToken
        );
        var second = await invoker.InvokeAsync(
            Guid.CreateVersion7(),
            "source",
            "operation",
            new AIFunctionArguments(),
            cancellationToken
        );

        Assert.NotEqual(first, second);
        Assert.Equal(2, recorder.Created.Count);
        Assert.Equal(recorder.Created, recorder.Disposed);
    }

    [Fact]
    public void AddIntegrations_RegistersInvokerSingletonAndInvocationSessionScoped()
    {
        var services = new ServiceCollection();
        services.AddIntegrations(new ConfigurationBuilder().Build());

        var invoker = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IConnectionMcpToolInvoker)
        );
        var session = Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IConnectionMcpInvocationSession)
        );

        Assert.Equal(ServiceLifetime.Singleton, invoker.Lifetime);
        Assert.Equal(ServiceLifetime.Scoped, session.Lifetime);
    }

    private sealed class SessionRecorder
    {
        public List<Guid> Created { get; } = [];

        public List<Guid> Disposed { get; } = [];
    }

    private sealed class TrackingInvocationSession : IConnectionMcpInvocationSession, IDisposable
    {
        private readonly Guid _id = Guid.CreateVersion7();
        private readonly SessionRecorder _recorder;

        public TrackingInvocationSession(SessionRecorder recorder)
        {
            _recorder = recorder;
            _recorder.Created.Add(_id);
        }

        public ValueTask<object?> InvokeMcpToolAsync(
            Guid connectionId,
            string sourceId,
            string operationName,
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            return ValueTask.FromResult<object?>(_id);
        }

        public void Dispose()
        {
            _recorder.Disposed.Add(_id);
        }
    }
}
