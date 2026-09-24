using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Context;

/// <summary>
/// 后台 Agent 的最外层：从所属执行作用域派生后台 Agent 的作用域，并把它的执行数据注入运行选项。
/// The outermost layer of a background Agent: derives the background scope from the owning execution scope and injects its data into the run options.
/// </summary>
/// <remarks>
/// 后台 Agent 作用域的当前执行者是后台 Agent 自己，且没有交互通道。
/// The background scope's executor is the background Agent itself, and it has no interaction channel.
/// </remarks>
internal sealed class BackgroundAgentScopeAgent : DelegatingAIAgent
{
    private readonly Guid _agentId;
    private readonly EngineKind _engineKind;

    public BackgroundAgentScopeAgent(AIAgent innerAgent, Guid agentId, EngineKind engineKind)
        : base(innerAgent)
    {
        _agentId = agentId;
        _engineKind = engineKind;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var scope = ExecutionScope.Required.CreateBackgroundAgentScope(_agentId, _engineKind);
        using var pushed = scope.Push();
        return await InnerAgent
            .RunAsync(messages, session, ExecutionRunOptions.With(options, scope.Context), cancellationToken)
            .ConfigureAwait(false);
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        var scope = ExecutionScope.Required.CreateBackgroundAgentScope(_agentId, _engineKind);
        return scope.RunStreaming(
            InnerAgent.RunStreamingAsync(
                messages,
                session,
                ExecutionRunOptions.With(options, scope.Context),
                cancellationToken
            )
        );
    }
}
