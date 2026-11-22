using System.Text.Json;
using global::A2A;
using Agw.Agents.Application.A2A;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.Execution.Agents.ExternalAgents.A2A;
using Agw.Agents.ExternalAgents.A2A;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Tests.A2A;

internal sealed class A2ARemoteAgentTestClient : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public A2ARemoteAgentTestClient(
        A2ATestAgentServer server,
        A2ARemoteAgentOptions? options = null,
        Uri? target = null,
        A2AEndpointCredentials? credentials = null
    )
    {
        _services = A2AAgentCardClientTests.CreateServices();
        Revision = Guid.NewGuid();
        Agent = A2ARemoteAgent.Create(
            new Agent
            {
                Id = Guid.CreateVersion7(),
                Name = server.Card.Name,
                Description = server.Card.Description,
                Type = AgentType.External,
                ExternalAgentKind = EngineKind.A2A,
                A2AEndpoint = new AgentA2AEndpoint
                {
                    InterfaceUrl = (target ?? server.InterfaceUrl).AbsoluteUri,
                    ProtocolBinding = server.Card.SupportedInterfaces[0].ProtocolBinding,
                    Streaming = server.Card.Capabilities.Streaming == true,
                    InterfaceRevision = Revision,
                    Extra = new(),
                    Credentials = credentials is null ? [] : A2AEndpointCredentialsJson.Serialize(credentials),
                },
            },
            _services.GetRequiredService<IA2ARemoteHttpClientFactory>(),
            options ?? new(),
            TimeProvider.System,
            _services.GetRequiredService<ILogger<A2ARemoteAgent>>()
        );
    }

    public A2ARemoteAgent Agent { get; }
    public Guid Revision { get; }
    public A2ARemoteAgentSession Session { get; } = new();

    public IAsyncEnumerable<AgentResponseUpdate> StreamAsync(string text, CancellationToken cancellationToken) =>
        Agent.RunStreamingAsync(new ChatMessage(ChatRole.User, text), Session, cancellationToken: cancellationToken);

    public async Task<List<AgentResponseUpdate>> RunAsync(string text, CancellationToken cancellationToken) =>
        await StreamAsync(text, cancellationToken).ToListAsync(cancellationToken);

    public static bool IsResult(AgentResponseUpdate update) =>
        AgwMessageClassifier.IsResult(update.AdditionalProperties, update.Contents);

    public static Message ReadInput(A2ARecordedRequest request)
    {
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        var parameters = root.TryGetProperty("params", out var value) ? value : root;
        return parameters.GetProperty("message").Deserialize<Message>(A2AJsonUtilities.DefaultOptions)!;
    }

    public async ValueTask DisposeAsync()
    {
        await Agent.DisposeAsync();
        await _services.DisposeAsync();
    }
}
