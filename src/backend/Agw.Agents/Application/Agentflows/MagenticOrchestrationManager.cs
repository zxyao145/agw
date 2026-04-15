using Agw.Shared.Exceptions;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Application.Agentflows;

public class MagenticOrchestrationManager : RoundRobinGroupChatManager
{
    private readonly AIAgent _orchestrator;
    private readonly List<AIAgent> _workers;
    private readonly int _maxStallCount;
    private readonly int _maxResetCount;

    private int _consecutiveOrchestratorTurns;
    private int _stallCount;
    private int _resetCount;
    private string _lastWorkerOutput = string.Empty;
    private AIAgent? _lastSelectedWorker;

    public MagenticOrchestrationManager(
        IReadOnlyList<AIAgent> agents,
        int maxRounds = 10,
        int maxStallCount = 3,
        int maxResetCount = 2) : base(agents)
    {
        if (agents.Count < 2)
        {
            throw new AgwException(ErrorCodes.MagenticRequiresAtLeastTwoAgents, "Magentic pattern requires at least 2 agents (orchestrator + workers)");
        }

        MaximumIterationCount = maxRounds;
        _orchestrator = agents[0];
        _workers = agents.Skip(1).ToList();
        _maxStallCount = maxStallCount;
        _maxResetCount = maxResetCount;
    }

    protected override ValueTask<AIAgent> SelectNextAgentAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (history.Count == 0 || history.All(m => m.Role == ChatRole.User))
        {
            _consecutiveOrchestratorTurns = 1;
            return ValueTask.FromResult(_orchestrator);
        }

        var lastMessage = history.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastMessage == null)
        {
            return ValueTask.FromResult(_orchestrator);
        }

        var orchestratorShouldSpeak =
            _consecutiveOrchestratorTurns == 0 && history.Count(m => m.Role == ChatRole.Assistant) % 3 == 0 ||
            _stallCount > 0 ||
            history.Count >= MaximumIterationCount - 1;

        if (orchestratorShouldSpeak)
        {
            _consecutiveOrchestratorTurns++;
            return ValueTask.FromResult(_orchestrator);
        }

        _consecutiveOrchestratorTurns = 0;

        var workerIndex = history.Count(m => m.Role == ChatRole.Assistant && m.AuthorName != _orchestrator.Name) % _workers.Count;
        var selectedWorker = _workers[workerIndex];

        if (_lastSelectedWorker == selectedWorker)
        {
            var currentOutput = lastMessage.Text ?? string.Empty;
            if (AreSimilarOutputs(_lastWorkerOutput, currentOutput))
            {
                _stallCount++;
                if (_stallCount >= _maxStallCount)
                {
                    _resetCount++;
                    _stallCount = 0;
                    return ValueTask.FromResult(_orchestrator);
                }
            }
            else
            {
                _stallCount = 0;
            }
        }

        _lastSelectedWorker = selectedWorker;
        _lastWorkerOutput = lastMessage.Text ?? string.Empty;
        return ValueTask.FromResult(selectedWorker);
    }

    protected override ValueTask<bool> ShouldTerminateAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        if (history.Count >= MaximumIterationCount || _resetCount > _maxResetCount)
        {
            return ValueTask.FromResult(true);
        }

        var lastMessage = history.LastOrDefault(m => m.Role == ChatRole.Assistant && m.AuthorName == _orchestrator.Name);
        if (lastMessage == null)
        {
            return ValueTask.FromResult(false);
        }

        var content = lastMessage.Text?.ToLowerInvariant() ?? string.Empty;
        var indicatesCompletion =
            content.Contains("task completed") ||
            content.Contains("task complete") ||
            content.Contains("finished") ||
            content.Contains("done") ||
            content.Contains("final answer") ||
            content.Contains("final result");

        return ValueTask.FromResult(indicatesCompletion);
    }

    private static bool AreSimilarOutputs(string output1, string output2)
    {
        if (string.IsNullOrWhiteSpace(output1) || string.IsNullOrWhiteSpace(output2))
        {
            return false;
        }

        var normalized1 = output1.Trim().ToLowerInvariant();
        var normalized2 = output2.Trim().ToLowerInvariant();
        if (Math.Abs(normalized1.Length - normalized2.Length) > normalized1.Length * 0.2)
        {
            return false;
        }

        return normalized1.Contains(normalized2[..Math.Min(50, normalized2.Length)]) ||
               normalized2.Contains(normalized1[..Math.Min(50, normalized1.Length)]);
    }
}
