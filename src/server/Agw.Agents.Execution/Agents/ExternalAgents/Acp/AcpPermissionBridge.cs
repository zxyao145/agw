using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Agents.Execution.Agents.ExternalAgents.Acp.Protocol;
using Agw.Agents.Execution.HumanInteraction;
using Agw.Agents.Execution.HumanInteraction.Application;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal sealed class AcpPermissionBridge
{
    private readonly HumanInteractionContextAccessor? _contextAccessor;
    private readonly bool _allowInteraction;
    private readonly AgwPermissionMode? _permissionMode;
    private readonly string _workingDirectory;
    private readonly Guid _agentId;
    private readonly IAgentExecutionContextAccessor? _executionContext;
    private readonly ExternalToolApprovalCache _cache;
    private readonly ILogger _logger;
    private RunBinding? _binding;

    public AcpPermissionBridge(
        HumanInteractionContextAccessor? contextAccessor,
        bool allowInteraction,
        AgwPermissionMode? permissionMode,
        string workingDirectory,
        Guid agentId,
        IAgentExecutionContextAccessor? executionContext,
        ExternalToolApprovalCache cache,
        ILogger logger
    )
    {
        _contextAccessor = contextAccessor;
        _allowInteraction = allowInteraction;
        _permissionMode = permissionMode;
        _workingDirectory = workingDirectory;
        _agentId = agentId;
        _executionContext = executionContext;
        _cache = cache;
        _logger = logger;
    }

    public async Task<AgentResponse> BindRunAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        CancellationToken cancellationToken
    )
    {
        Bind();
        try
        {
            return await innerAgent.RunAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _binding, null);
        }
    }

    public async IAsyncEnumerable<AgentResponseUpdate> BindRunStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        Bind();
        try
        {
            await foreach (
                var update in innerAgent
                    .RunStreamingAsync(messages, session, options, cancellationToken)
                    .ConfigureAwait(false)
            )
                yield return update;
        }
        finally
        {
            Volatile.Write(ref _binding, null);
        }
    }

    public async ValueTask<AcpPermissionDecision> DecideAsync(
        string toolName,
        string? title,
        JsonElement? rawInput,
        string toolCallId,
        CancellationToken ct
    )
    {
        try
        {
            return await DecideCoreAsync(toolName, title, rawInput, toolCallId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return AcpPermissionDecision.Cancelled;
        }
    }

    public AcpRequestPermissionResponse Respond(
        IReadOnlyList<AcpPermissionOption> options,
        AcpPermissionDecision decision
    )
    {
        AcpClientDiagnostics.PermissionRequests.Add(
            1,
            new TagList
            {
                { "decision", decision.ToString().ToLowerInvariant() },
                { "mode", _permissionMode?.ToString() ?? "default" },
            }
        );
        return AcpPermissionOptions.ToResponse(options, decision);
    }

    private async ValueTask<AcpPermissionDecision> DecideCoreAsync(
        string toolName,
        string? title,
        JsonElement? rawInput,
        string toolCallId,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        var binding = Volatile.Read(ref _binding);
        if (binding is null)
            return AcpPermissionDecision.Rejected;
        if (_permissionMode == AgwPermissionMode.FullAccess)
            return AcpPermissionDecision.Allowed;

        var scope =
            _permissionMode == AgwPermissionMode.AllowSameArguments
            && rawInput is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) }
                ? binding.Scope
                : null;
        var arguments = scope is null ? null : JsonNode.Parse(rawInput!.Value.GetRawText());
        if (scope is not null && _cache.Contains(scope, binding.Version, toolName, arguments))
            return AcpPermissionDecision.Allowed;
        if (binding.Handler is null)
        {
            _logger.LogInformation(
                "ACP agent {AgentId} requested permission without an active interaction channel",
                _agentId
            );
            return AcpPermissionDecision.Rejected;
        }
        var request = new ToolApprovalInteraction
        {
            InteractionId = Guid.CreateVersion7().ToString("N"),
            Prompt = $"Allow the ACP agent to run \"{title ?? toolName}\"?",
            Arguments = rawInput,
            Source = binding.Source with { ToolName = toolName, CallId = toolCallId },
        };
        var resolution = await binding.Handler.ResolveAsync(request, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (resolution is not InteractionResolution.Resolved { Response: ToolApprovalDecision { Approved: true } })
            return AcpPermissionDecision.Rejected;
        if (scope is not null)
            _cache.Add(scope, binding.Version, toolName, arguments);
        return AcpPermissionDecision.Allowed;
    }

    private void Bind()
    {
        var (source, scope, version) = ExternalToolApprovalCache.BindScope(
            _contextAccessor?.Source,
            _executionContext?.Current,
            _agentId,
            _workingDirectory
        );
        var handler = _allowInteraction ? _contextAccessor?.Current as IInteractionHandler : null;
        var binding = new RunBinding(handler, source, scope, version);
        if (Interlocked.CompareExchange(ref _binding, binding, null) is not null)
            throw new AgwException(
                ErrorCodes.AgentExecutionFailed,
                "Concurrent ACP turns cannot share a permission bridge."
            );
    }

    private sealed record RunBinding(
        IInteractionHandler? Handler,
        InteractionSource Source,
        string? Scope,
        long Version
    );
}

internal enum AcpPermissionDecision
{
    Allowed,
    Rejected,
    Cancelled,
}

internal static class AcpPermissionOptions
{
    public static AcpRequestPermissionResponse ToResponse(
        IReadOnlyList<AcpPermissionOption> options,
        AcpPermissionDecision decision
    )
    {
        var option = decision switch
        {
            AcpPermissionDecision.Allowed => Find(
                options,
                AcpPermissionOptionKinds.AllowOnce,
                AcpPermissionOptionKinds.AllowAlways
            ),
            AcpPermissionDecision.Rejected => Find(
                options,
                AcpPermissionOptionKinds.RejectOnce,
                AcpPermissionOptionKinds.RejectAlways
            ),
            _ => null,
        };
        return option is null ? new(new("cancelled")) : new(new("selected", option.OptionId));
    }

    private static AcpPermissionOption? Find(IReadOnlyList<AcpPermissionOption> options, string first, string second) =>
        options.FirstOrDefault(option => option.Kind == first)
        ?? options.FirstOrDefault(option => option.Kind == second);
}
