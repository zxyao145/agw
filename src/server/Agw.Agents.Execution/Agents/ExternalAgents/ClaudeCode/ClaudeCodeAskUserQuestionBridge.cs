using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.Execution.Turns;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Agw.Tools.HumanInteraction;
using Agw.Tools.Impl.Tools.Basic;
using ClaudeCodeSdk.Types;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Agents.ExternalAgents.ClaudeCode;

internal sealed class ClaudeCodeAskUserQuestionBridge
{
    private const string ToolName = "AskUserQuestion";
    private const string Prompt = "The agent needs your input to continue.";

    private readonly HumanInteractionContextAccessor? _contextAccessor;
    private readonly bool _allowInteraction;
    private IHumanInteractionChannel? _activeChannel;
    private int _isBound;
    private readonly AgwPermissionMode? _permissionMode;
    private readonly string? _workingDirectory;
    private readonly Guid _agentId;
    private readonly IRuntimeTurnContextAccessor? _turnContext;
    private readonly ClaudeToolApprovalCache _cache;
    private string? _scope;
    private long _version;
    private InteractionSource _source = new();

    public ClaudeCodeAskUserQuestionBridge(
        HumanInteractionContextAccessor? contextAccessor,
        bool allowInteraction,
        AgwPermissionMode? permissionMode = null,
        string? workingDirectory = null,
        Guid agentId = default,
        IRuntimeTurnContextAccessor? turnContext = null,
        ClaudeToolApprovalCache? cache = null
    )
    {
        _contextAccessor = contextAccessor;
        _allowInteraction = allowInteraction;
        _permissionMode = permissionMode;
        _workingDirectory = workingDirectory;
        _agentId = agentId;
        _turnContext = turnContext;
        _cache = cache ?? new ClaudeToolApprovalCache();
    }

    public async Task<AgentResponse> BindRunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        using var binding = BindCurrentChannel(options);
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
        using var binding = BindCurrentChannel(options);
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
            return await HandleToolApprovalAsync(toolName, input, context, cancellationToken).ConfigureAwait(false);
        }

        var channel = Volatile.Read(ref _activeChannel);
        if (channel == null)
        {
            return Deny("AskUserQuestion requires an active interactive channel.");
        }

        try
        {
            // 将 Claude 原生 AskUserQuestion 转为 UserInput；FullAccess 下仍需要用户提供真实答案。
            // 只发送问题，忽略原始 input 中可能携带的模型答案；tool_use_id 保留为来源调用标识。
            // channel 负责交互 ID 与等待，校验后的用户答案才会作为 updatedInput 返回 SDK。
            var toolParams =
                JsonUtil.Deserialize<AskUserQuestionToolParams>(input.GetRawText())
                ?? throw new AgwException(ErrorCodes.InvalidParam, "Question arguments are invalid.");
            AskUserQuestionTool.ValidateQuestions(toolParams.Questions);
            var questions = input.GetProperty("questions").Clone();
            var payload = JsonSerializer.SerializeToElement(
                new Dictionary<string, JsonElement> { ["questions"] = questions }
            );
            var request = new UserInputRequest("questions", Prompt, payload)
            {
                Source = new InteractionSource { ToolName = ToolName, CallId = context.ToolUseId },
            };
            var response = await channel.RequestAsync(request, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<PermissionResult> HandleToolApprovalAsync(
        string toolName,
        JsonElement input,
        ToolPermissionContext context,
        CancellationToken cancellationToken
    )
    {
        if (Volatile.Read(ref _isBound) == 0)
            return Deny("Tool approval requires an active run.");
        cancellationToken.ThrowIfCancellationRequested();
        if (_permissionMode == AgwPermissionMode.FullAccess)
            return new PermissionResultAllow();
        var arguments = JsonNode.Parse(input.GetRawText());
        if (
            _permissionMode == AgwPermissionMode.AllowSameArguments
            && _scope != null
            && _cache.Contains(_scope, _version, toolName, arguments)
        )
            return new PermissionResultAllow();
        if (Volatile.Read(ref _activeChannel) is not IInteractionHandler handler)
            return Deny("Tool approval requires an active interactive channel.");
        var request = new ToolApprovalInteraction
        {
            InteractionId = Guid.CreateVersion7().ToString("N"),
            Prompt = $"Allow Claude Code to use {toolName}?",
            Arguments = input.Clone(),
            Source = _source with { ToolName = toolName, CallId = context.ToolUseId },
        };
        var result = await handler.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (result is not InteractionResolution.Resolved { Response: ToolApprovalDecision { Approved: true } })
            return Deny("Tool execution was denied.");
        if (_permissionMode == AgwPermissionMode.AllowSameArguments && _scope != null)
            _cache.Add(_scope, _version, toolName, arguments);
        return new PermissionResultAllow();
    }

    private IDisposable BindCurrentChannel(AgentRunOptions? options)
    {
        if (Interlocked.CompareExchange(ref _isBound, 1, 0) != 0)
        {
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Concurrent Claude Code runs cannot share a human interaction bridge."
            );
        }

        _source =
            options?.AdditionalProperties?.TryGetValue(HumanInteractionToolMetadata.SourceKey, out var source) == true
            && source is InteractionSource attribution
                ? attribution
                : new();
        var turn = _turnContext?.Current;
        _scope =
            turn == null
                ? null
                : JsonSerializer.Serialize(
                    new
                    {
                        turn.UserId,
                        turn.ProjectId,
                        turn.ProjectConversationId,
                        turn.Task.Generation,
                        AgentId = _agentId,
                        _source.NodeId,
                        Workspace = _workingDirectory,
                    }
                );
        _version = turn?.Settings.PermissionVersion ?? 0;
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
