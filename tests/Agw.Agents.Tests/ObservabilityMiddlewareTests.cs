using System.Diagnostics;
using Agw.Agents.Execution.Agents.Middleware;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Tests;

public class ObservabilityMiddlewareTests
{
    [Fact]
    public async Task LogRunMiddleware_AgentCompletes_LogsNameInputAndOutput()
    {
        var logger = new CapturingLogger<ObservabilityMiddleware>();
        var middleware = new ObservabilityMiddleware(logger);
        var agent = CreateAgent("persisted-agent");
        var input = new List<ChatMessage> { new(ChatRole.User, "hello") };

        var response = await middleware.LogRunMiddleware(
            input,
            session: null,
            options: null,
            agent,
            TestContext.Current.CancellationToken
        );

        Assert.Equal("response", response.Text);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Information && Equals(entry.GetProperty("AgentName"), "persisted-agent")
        );
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Debug
                && entry.Message.Contains("input", StringComparison.OrdinalIgnoreCase)
                && Equals(entry.GetProperty("AgentName"), "persisted-agent")
        );
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Debug
                && entry.Message.Contains("output", StringComparison.OrdinalIgnoreCase)
                && Equals(entry.GetProperty("AgentName"), "persisted-agent")
        );
    }

    [Fact]
    public async Task LogStreamingMiddleware_AgentCompletes_ForwardsUpdatesAndLogsOutput()
    {
        var logger = new CapturingLogger<ObservabilityMiddleware>();
        var middleware = new ObservabilityMiddleware(logger);
        var agent = CreateAgent("persisted-agent");
        var input = new List<ChatMessage> { new(ChatRole.User, "hello") };
        var updates = new List<AgentResponseUpdate>();

        await foreach (
            var update in middleware.LogStreamingMiddleware(
                input,
                session: null,
                options: null,
                agent,
                TestContext.Current.CancellationToken
            )
        )
        {
            updates.Add(update);
        }

        Assert.Equal("response", updates.ToAgentResponse().Text);
        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Information && Equals(entry.GetProperty("AgentName"), "persisted-agent")
        );
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Debug && entry.Message.Contains("input", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Debug && entry.Message.Contains("output", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task LogRunMiddleware_WorkflowExecutorSpan_DoesNotAddAgentNameTag()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.Agents.AI.Workflows",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("Microsoft.Agents.AI.Workflows");
        using var activity = source.StartActivity("executor.process node-alias");
        Assert.NotNull(activity);

        var middleware = new ObservabilityMiddleware(new CapturingLogger<ObservabilityMiddleware>());
        var agent = CreateAgent("persisted-agent");

        await middleware.LogRunMiddleware(
            [new ChatMessage(ChatRole.User, "hello")],
            session: null,
            options: null,
            agent,
            TestContext.Current.CancellationToken
        );

        Assert.Null(activity.GetTagItem("gen_ai.agent.name"));
    }

    private static AIAgent CreateAgent(string name)
    {
        return new ChatClientAgent(new StubChatClient(), new ChatClientAgentOptions { Id = "agent-id", Name = name });
    }

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "response")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "response");
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            var properties = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), properties.ToList()));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> Properties
    )
    {
        public object? GetProperty(string name)
        {
            return Properties
                .FirstOrDefault(property => string.Equals(property.Key.TrimStart('@'), name, StringComparison.Ordinal))
                .Value;
        }
    }
}
