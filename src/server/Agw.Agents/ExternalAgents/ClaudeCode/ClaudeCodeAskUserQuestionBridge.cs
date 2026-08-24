using System.Runtime.CompilerServices;
using System.Text.Json;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Agw.Tools.Impl.Basic;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.ExternalAgents.ClaudeCode;

internal sealed class ClaudeCodeAskUserQuestionBridge
{
    private const string ToolName = "AskUserQuestion";
    private const string Prompt = "The agent needs your input to continue.";

    private readonly HumanInteractionContextAccessor? _contextAccessor;
    private readonly bool _allowInteraction;
    private IHumanInteractionChannel? _activeChannel;
    private int _isBound;

    public ClaudeCodeAskUserQuestionBridge(HumanInteractionContextAccessor? contextAccessor, bool allowInteraction)
    {
        _contextAccessor = contextAccessor;
        _allowInteraction = allowInteraction;
    }

    public async Task<AgentResponse> BindRunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        using var binding = BindCurrentChannel();
        return await innerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AgentResponseUpdate> BindRunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var binding = BindCurrentChannel();
        await foreach (
            var update in innerAgent
                .RunStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return update;
        }
    }

    public async ValueTask<PermissionResult> HandleAsync(
        string toolName,
        JsonElement input,
        ToolPermissionContext context,
        CancellationToken cancellationToken
    )
    {
        if (!string.Equals(toolName, ToolName, StringComparison.Ordinal))
        {
            return Deny($"Agw does not handle Claude Code permission request '{toolName}'.");
        }

        var channel = Volatile.Read(ref _activeChannel);
        if (channel == null)
        {
            return Deny("AskUserQuestion requires an active interactive channel.");
        }

        try
        {
            var toolParams =
                JsonUtil.Deserialize<AskUserQuestionToolParams>(input.GetRawText())
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Question arguments are invalid.");
            AskUserQuestionTool.ValidateQuestions(toolParams.Questions);
            var questions = input.GetProperty("questions").Clone();
            var payload = JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement> { ["questions"] = questions }
            );
            var request = new HumanInteractionRequest(Guid.CreateVersion7().ToString("N"), "questions", Prompt, payload)
            {
                ToolName = ToolName,
                CallId = context.ToolUseId,
            };
            var response = await channel.RequestAsync(request, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(request.RequestId, response.RequestId, StringComparison.Ordinal))
            {
                return Deny(
                    $"Human interaction response '{response.RequestId}' does not match request '{request.RequestId}'."
                );
            }

            if (response.Cancelled)
            {
                return Deny("User cancelled the question request without answering.");
            }

            if (!response.ResponseData.HasValue)
            {
                return Deny("Question response data is required.");
            }

            var responseData =
                JsonUtil.Deserialize<AskUserQuestionResponseData>(response.ResponseData.Value.GetRawText())
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Question response data is invalid.");
            var answers = AskUserQuestionTool.ValidateAnswers(
                toolParams.Questions,
                responseData.Answers,
                responseData.Annotations
            );
            var updatedInput = JsonSerializer.SerializeToElement(
                new Dictionary<string, object?> { ["questions"] = questions, ["answers"] = answers }
            );
            return new PermissionResultAllow(updatedInput);
        }
        catch (AgwException exception)
        {
            return Deny(exception.Message);
        }
        catch (JsonException exception)
        {
            return Deny($"Question payload is invalid: {exception.Message}");
        }
    }

    private IDisposable BindCurrentChannel()
    {
        if (Interlocked.CompareExchange(ref _isBound, 1, 0) != 0)
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Concurrent Claude Code runs cannot share a human interaction bridge."
            );
        }

        Volatile.Write(ref _activeChannel, _allowInteraction ? _contextAccessor?.Current : null);
        return new Binding(this);
    }

    private void Unbind()
    {
        Volatile.Write(ref _activeChannel, null);
        Volatile.Write(ref _isBound, 0);
    }

    private static PermissionResultDeny Deny(string message) => new(message, Interrupt: false);

    private sealed class Binding : IDisposable
    {
        private readonly ClaudeCodeAskUserQuestionBridge _owner;
        private int _disposed;

        public Binding(ClaudeCodeAskUserQuestionBridge owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unbind();
            }
        }
    }

    private sealed class AskUserQuestionResponseData
    {
        public Dictionary<string, string>? Answers { get; set; }

        public Dictionary<string, AskUserQuestionAnnotation>? Annotations { get; set; }
    }
}
