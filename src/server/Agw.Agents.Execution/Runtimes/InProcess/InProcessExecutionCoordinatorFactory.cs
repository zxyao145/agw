using Agw.Agents.Execution.Agentflows.Turns;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Agents.Execution.Agents.Turns;
using Agw.Agents.Execution.Context;
using Agw.Agents.Execution.Turns;
using Agw.Files.Abstracts;
using Agw.Projects.Contracts.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Execution.Runtimes.InProcess;

/// <summary>
/// 为一条连接或一次 Facade 调用创建进程内协调器；协调器保留的 Runtime 使用本工厂所在 DI 作用域的服务，Turn 的消息写到受理时建立的广播。
/// Creates the in-process coordinator of one connection or one Facade call; the Runtime it retains uses services of this factory's DI scope, and turn messages go to the broadcast created at acceptance.
/// </summary>
internal sealed class InProcessExecutionCoordinatorFactory
{
    private readonly IAgentRuntimeFactory _agentRuntimes;
    private readonly AgentTurnExecutor _agentTurns;
    private readonly AgentflowTurnExecutor _agentflowTurns;
    private readonly ExecutionContextFactory _executionContexts;
    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly TurnBroadcastRegistry _broadcasts;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConversationExecutionGate? _conversationGate;

    public InProcessExecutionCoordinatorFactory(
        IAgentRuntimeFactory agentRuntimes,
        AgentTurnExecutor agentTurns,
        AgentflowTurnExecutor agentflowTurns,
        ExecutionContextFactory executionContexts,
        IAgwFileSystemResolver fileSystemResolver,
        TurnBroadcastRegistry broadcasts,
        IServiceScopeFactory scopeFactory,
        IConversationExecutionGate? conversationGate = null
    )
    {
        _agentRuntimes = agentRuntimes;
        _agentTurns = agentTurns;
        _agentflowTurns = agentflowTurns;
        _executionContexts = executionContexts;
        _fileSystemResolver = fileSystemResolver;
        _broadcasts = broadcasts;
        _scopeFactory = scopeFactory;
        _conversationGate = conversationGate;
    }

    public InProcessExecutionCoordinator Create(
        CancellationToken hostToken,
        Action<int>? pendingInteractionCountChanged = null
    ) =>
        new(
            _agentRuntimes,
            _agentTurns,
            _agentflowTurns,
            _executionContexts,
            _fileSystemResolver,
            _conversationGate,
            _broadcasts,
            _scopeFactory,
            hostToken,
            pendingInteractionCountChanged
        );
}
