using System.Collections.Concurrent;
using System.Net;
using global::A2A;
using global::A2A.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Tests.A2A;

internal sealed record A2ATestAgentSetup(string ProtocolBinding, bool Streaming, IAgentHandler Handler)
{
    public IReadOnlyDictionary<string, string>? RequiredCardHeaders { get; init; }
    public IReadOnlyDictionary<string, string>? RequiredInterfaceHeaders { get; init; }
}

internal sealed record A2ARecordedRequest
{
    public required string Method { get; init; }
    public required string PathAndQuery { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required string Body { get; init; }
}

internal sealed class A2ATestAgentServer : IAsyncDisposable
{
    private const string AgentPath = "/agent";
    private const string CardPath = AgentPath + "/.well-known/agent-card.json";
    private readonly WebApplication _app;
    private readonly A2ATestAgentSetup _setup;
    private readonly ConcurrentQueue<A2ARecordedRequest> _requests = new();
    private readonly ConcurrentDictionary<HttpContext, TaskCompletionSource> _streams = new();

    private A2ATestAgentServer(WebApplication app, A2ATestAgentSetup setup, AgentCard card)
    {
        _app = app;
        _setup = setup;
        Card = card;
    }

    public Uri InterfaceUrl => new(new Uri(_app.Urls.Single()), AgentPath);
    public Uri CardUrl => new(InterfaceUrl, CardPath);
    public AgentCard Card { get; }
    public string? CardBodyOverride { get; set; }
    public string? RedirectLocation { get; set; }
    public string? SetCookie { get; set; }
    public TimeSpan CardDelay { get; set; }
    public IReadOnlyList<A2ARecordedRequest> Requests => _requests.ToArray();
    public IReadOnlyList<string> CanceledTaskIds => (_setup.Handler as ScriptedAgentHandler)?.CanceledTaskIds ?? [];
    public ITaskStore TaskStore => _app.Services.GetRequiredService<ITaskStore>();

    public static async Task<A2ATestAgentServer> StartAsync(A2ATestAgentSetup setup, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ITaskStore, InMemoryTaskStore>();
        builder.Services.AddSingleton<IA2ARequestHandler>(services => new A2AServer(
            setup.Handler,
            services.GetRequiredService<ITaskStore>(),
            new ChannelEventNotifier(),
            services.GetRequiredService<ILogger<A2AServer>>()
        ));
        var app = builder.Build();
        var card = new AgentCard
        {
            Name = "test-agent",
            Description = "Test agent",
            Version = "1.0.0",
            Capabilities = new AgentCapabilities { Streaming = setup.Streaming },
        };
        var server = new A2ATestAgentServer(app, setup, card);
        app.Use(server.RecordAndAuthorizeAsync);
        var handler = app.Services.GetRequiredService<IA2ARequestHandler>();
        if (setup.ProtocolBinding == ProtocolBindingNames.HttpJson)
        {
            app.MapHttpA2A(handler, card, AgentPath);
        }
        else
        {
            app.MapA2A(handler, AgentPath);
        }
        app.MapWellKnownAgentCard(card, AgentPath);
        try
        {
            await app.StartAsync(ct);
            card.SupportedInterfaces.Add(
                new AgentInterface
                {
                    Url = server.InterfaceUrl.AbsoluteUri,
                    ProtocolBinding = setup.ProtocolBinding,
                    ProtocolVersion = "1.0",
                }
            );
            return server;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async Task DropActiveStreamsAsync(CancellationToken cancellationToken)
    {
        var streams = _streams.ToArray();
        foreach (var (context, _) in streams)
        {
            context.Abort();
        }
        await Task.WhenAll(streams.Select(stream => stream.Value.Task))
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _app.StopAsync(cancellationToken);

    private async Task RecordAndAuthorizeAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        await RecordRequestAsync(request, context.RequestAborted);

        var cardRequest = request.Path.Value?.TrimEnd('/') == CardPath;
        var requiredHeaders = cardRequest ? _setup.RequiredCardHeaders : _setup.RequiredInterfaceHeaders;
        if (requiredHeaders?.Any(header => request.Headers[header.Key].ToString() != header.Value) == true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (SetCookie is { } cookie)
        {
            context.Response.Headers.SetCookie = cookie;
        }

        if (cardRequest)
        {
            await Task.Delay(CardDelay, context.RequestAborted);
            if (RedirectLocation is { } redirect)
            {
                context.Response.Redirect(redirect);
                return;
            }
            if (CardBodyOverride is { } cardBody)
            {
                await context.Response.WriteAsync(cardBody, context.RequestAborted);
                return;
            }
        }

        var streaming = request.Headers.Accept.Any(value =>
            value?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true
        );
        var streamClosed = streaming
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        if (streamClosed is not null)
        {
            _streams.TryAdd(context, streamClosed);
        }
        try
        {
            await next(context);
        }
        finally
        {
            if (streamClosed is not null)
            {
                _streams.TryRemove(context, out _);
                streamClosed.SetResult();
            }
        }
    }

    private async Task RecordRequestAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering(bufferThreshold: int.MaxValue);
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken);
        request.Body.Position = 0;
        _requests.Enqueue(
            new A2ARecordedRequest
            {
                Method = request.Method,
                PathAndQuery = request.Path + request.QueryString,
                Headers = request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase
                ),
                Body = body,
            }
        );
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

internal sealed class ScriptedAgentHandler : IAgentHandler
{
    private readonly Func<RequestContext, AgentEventQueue, CancellationToken, Task> _execute;
    private readonly ConcurrentQueue<string> _canceledTaskIds = new();

    public ScriptedAgentHandler(Func<RequestContext, AgentEventQueue, CancellationToken, Task> execute)
    {
        _execute = execute;
    }

    public IReadOnlyList<string> CanceledTaskIds => _canceledTaskIds.ToArray();

    public Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken) =>
        _execute(context, eventQueue, cancellationToken);

    public async Task CancelAsync(
        RequestContext context,
        AgentEventQueue eventQueue,
        CancellationToken cancellationToken
    )
    {
        _canceledTaskIds.Enqueue(context.TaskId);
        await new TaskUpdater(eventQueue, context.TaskId, context.ContextId).CancelAsync(cancellationToken);
    }

    public static ScriptedAgentHandler Echo() =>
        new(
            (context, queue, ct) =>
                new MessageResponder(queue, context.ContextId).ReplyAsync(context.UserText ?? string.Empty, ct).AsTask()
        );

    public static ScriptedAgentHandler StreamArtifacts(Task finish, bool partial = false) =>
        new(
            async (context, queue, ct) =>
            {
                var updater = new TaskUpdater(queue, context.TaskId, context.ContextId);
                await updater.SubmitAsync(ct);
                await updater.StartWorkAsync(cancellationToken: ct);
                await updater.AddArtifactAsync([Part.FromText("one")], "one", cancellationToken: ct);
                if (partial)
                {
                    await updater.AddArtifactAsync(
                        [Part.FromText("partial")],
                        "partial",
                        lastChunk: false,
                        cancellationToken: ct
                    );
                }
                await finish.WaitAsync(ct);
                await updater.AddArtifactAsync([Part.FromText("two")], "two", cancellationToken: ct);
                await updater.CompleteAsync(cancellationToken: ct);
            }
        );
}
