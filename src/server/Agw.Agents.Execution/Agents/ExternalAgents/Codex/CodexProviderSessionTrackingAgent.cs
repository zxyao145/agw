using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.CodexSdk.AppServer;
using OpenAI.CodexSdk.AppServer.MAF;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Codex;

internal sealed class CodexProviderSessionTrackingAgent : DelegatingAIAgent
{
    private readonly CodexAppServerClient _client;
    private readonly CodexAppServerAIAgentOptions _options;
    private readonly Guid? _providerSessionId;
    private readonly bool _isResume;

    public CodexProviderSessionTrackingAgent(
        CodexAppServerClient client,
        CodexAppServerAIAgentOptions options,
        Guid? providerSessionId,
        bool isResume
    )
        : base(new CodexAppServerAIAgent(client, options))
    {
        _client = client;
        _options = options;
        _providerSessionId = providerSessionId;
        _isResume = isResume;
    }

    protected override async ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default
    )
    {
        var session = await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!_isResume || _providerSessionId is not { } providerSessionId)
        {
            return session;
        }

        var state = JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>
            {
                ["threadId"] = providerSessionId.Normalize(),
                ["stateBag"] = session.StateBag.Serialize(),
            }
        );
        return await InnerAgent
            .DeserializeSessionAsync(state, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default
    ) => InnerAgent.SerializeSessionAsync(session, jsonSerializerOptions, cancellationToken);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default
    ) => InnerAgent.DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken);

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var input = PrepareInput(messages, _options);
        var agent = new CodexAppServerAIAgent(_client, input.Options);
        var response = await agent.RunAsync(input.Messages, session, options, cancellationToken).ConfigureAwait(false);
        if (response.RawRepresentation is IEnumerable<OpenAI.CodexSdk.AppServer.AppServerNotification> notifications)
        {
            foreach (var message in response.Messages.Where(message => message.Role == ChatRole.Assistant))
            {
                message.RawRepresentation = notifications;
            }
        }

        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var input = PrepareInput(messages, _options);
        var agent = new CodexAppServerAIAgent(_client, input.Options);
        await foreach (
            var update in agent
                .RunStreamingAsync(input.Messages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return update;
        }
    }

    /// <summary>
    /// 每轮使用独立的 thread instructions，创建和恢复 thread 时应用当前平台指令。
    /// Uses independent thread instructions for each run, applying current platform instructions on start and resume.
    /// </summary>
    internal static (IReadOnlyList<ChatMessage> Messages, CodexAppServerAIAgentOptions Options) PrepareInput(
        IEnumerable<ChatMessage> messages,
        CodexAppServerAIAgentOptions options
    )
    {
        var input = new List<ChatMessage>();
        var instructions = new List<string>();
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.System && message.Role.Value != "developer")
            {
                input.Add(message);
                continue;
            }
            if (message.Contents.Any(content => content is not TextContent))
                throw new AgwException(ErrorCodes.InvalidParam, "Codex thread instructions support only text content.");
            if (!string.IsNullOrWhiteSpace(message.Text))
                instructions.Add(message.Text);
        }

        var configured = options with
        {
            ThreadStartParams = options.ThreadStartParams with
            {
                DeveloperInstructions = MergeInstructions(
                    options.ThreadStartParams.DeveloperInstructions,
                    instructions
                ),
            },
            ConfigureThreadResume = parameters =>
            {
                var resumed = options.ConfigureThreadResume is { } configure ? configure(parameters) : parameters;
                return resumed with
                {
                    DeveloperInstructions = MergeInstructions(
                        resumed.DeveloperInstructions.IsDefined
                            ? resumed.DeveloperInstructions
                            : options.ThreadStartParams.DeveloperInstructions,
                        instructions
                    ),
                };
            },
        };
        return (input, configured);
    }

    private static Optional<string?> MergeInstructions(Optional<string?> configured, IReadOnlyList<string> instructions)
    {
        var values =
            configured.IsDefined && !string.IsNullOrWhiteSpace(configured.Value)
                ? new[] { configured.Value }.Concat(instructions)
                : instructions;
        return new Optional<string?>(string.Join("\n\n", values));
    }
}
