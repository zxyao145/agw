using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace DSystem.Domain.Services;

/// <summary>
/// Custom GroupChat manager implementing Magentic-One orchestration pattern.
/// The orchestrator agent (first in list) coordinates task distribution among worker agents.
/// </summary>
public class MagenticOrchestrationManager : RoundRobinGroupChatManager
{
    private readonly AIAgent _orchestrator;
    private readonly List<AIAgent> _workers;
    private readonly int _maxStallCount;
    private readonly int _maxResetCount;

    private int _consecutiveOrchestratorTurns = 0;
    private int _stallCount = 0;
    private int _resetCount = 0;
    private string _lastWorkerOutput = string.Empty;
    private AIAgent? _lastSelectedWorker = null;

    /// <summary>
    /// Creates a new MagenticOrchestrationManager.
    /// </summary>
    /// <param name="agents">All agents (first must be orchestrator, rest are workers)</param>
    /// <param name="maxRounds">Maximum collaboration rounds (default: 10)</param>
    /// <param name="maxStallCount">Maximum rounds without progress before intervention (default: 3)</param>
    /// <param name="maxResetCount">Maximum allowed plan resets (default: 2)</param>
    public MagenticOrchestrationManager(
        IReadOnlyList<AIAgent> agents,
        int maxRounds = 10,
        int maxStallCount = 3,
        int maxResetCount = 2) : base(agents)
    {
        if (agents.Count < 2)
        {
            throw new ArgumentException("Magentic pattern requires at least 2 agents (orchestrator + workers)", nameof(agents));
        }

        MaximumIterationCount = maxRounds;
        _orchestrator = agents[0];
        _workers = agents.Skip(1).ToList();
        _maxStallCount = maxStallCount;
        _maxResetCount = maxResetCount;
    }

    /// <summary>
    /// Selects the next agent to speak based on Magentic orchestration logic.
    /// Orchestrator speaks periodically to coordinate, otherwise workers are selected.
    /// </summary>
    protected override async ValueTask<AIAgent> SelectNextAgentAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // First turn always goes to orchestrator for initial planning
        if (history.Count == 0 || history.All(m => m.Role == ChatRole.User))
        {
            _consecutiveOrchestratorTurns = 1;
            return _orchestrator;
        }

        var lastMessage = history.LastOrDefault(m => m.Role == ChatRole.Assistant);
        if (lastMessage == null)
        {
            return _orchestrator;
        }

        // Check if orchestrator should intervene
        // 1. After every 2-3 worker turns for coordination
        // 2. After detecting stall
        // 3. For final synthesis
        bool orchestratorShouldSpeak =
            _consecutiveOrchestratorTurns == 0 && history.Count(m => m.Role == ChatRole.Assistant) % 3 == 0 ||
            _stallCount > 0 ||
            history.Count >= MaximumIterationCount - 1;

        if (orchestratorShouldSpeak)
        {
            _consecutiveOrchestratorTurns++;
            return _orchestrator;
        }

        // Orchestrator has spoken, now select a worker
        // In a real implementation, the orchestrator's message would indicate which worker to use
        // For now, we use a simple strategy: rotate through workers
        _consecutiveOrchestratorTurns = 0;

        var workerIndex = (history.Count(m => m.Role == ChatRole.Assistant && m.AuthorName != _orchestrator.Name)) % _workers.Count;
        var selectedWorker = _workers[workerIndex];

        // Detect stall: same worker producing similar output repeatedly
        if (_lastSelectedWorker == selectedWorker)
        {
            var currentOutput = lastMessage.Text ?? string.Empty;
            if (AreSimilarOutputs(_lastWorkerOutput, currentOutput))
            {
                _stallCount++;
                if (_stallCount >= _maxStallCount)
                {
                    // Request orchestrator intervention
                    _resetCount++;
                    _stallCount = 0;
                    return _orchestrator;
                }
            }
            else
            {
                _stallCount = 0;
            }
        }

        _lastSelectedWorker = selectedWorker;
        _lastWorkerOutput = lastMessage.Text ?? string.Empty;

        return selectedWorker;
    }

    /// <summary>
    /// Determines if the workflow should terminate based on:
    /// - Maximum rounds reached
    /// - Maximum reset count exceeded
    /// - Orchestrator indicates completion
    /// </summary>
    protected override ValueTask<bool> ShouldTerminateAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        // Exceed maximum rounds
        if (history.Count >= MaximumIterationCount)
        {
            return new ValueTask<bool>(true);
        }

        // Exceed maximum resets (plan failures)
        if (_resetCount > _maxResetCount)
        {
            return new ValueTask<bool>(true);
        }

        // Check if orchestrator indicates task completion
        var lastMessage = history.LastOrDefault(m => m.Role == ChatRole.Assistant && m.AuthorName == _orchestrator.Name);
        if (lastMessage != null)
        {
            var content = lastMessage.Text?.ToLowerInvariant() ?? string.Empty;

            // Simple completion detection (could be enhanced with more sophisticated NLP)
            bool indicatesCompletion =
                content.Contains("task completed") ||
                content.Contains("task complete") ||
                content.Contains("finished") ||
                content.Contains("done") ||
                content.Contains("final answer") ||
                content.Contains("final result");

            if (indicatesCompletion)
            {
                return new ValueTask<bool>(true);
            }
        }

        return new ValueTask<bool>(false);
    }

    /// <summary>
    /// Compares two outputs to detect if they are similar (potential stall indicator).
    /// </summary>
    private bool AreSimilarOutputs(string output1, string output2)
    {
        if (string.IsNullOrWhiteSpace(output1) || string.IsNullOrWhiteSpace(output2))
        {
            return false;
        }

        // Normalize for comparison
        var normalized1 = output1.Trim().ToLowerInvariant();
        var normalized2 = output2.Trim().ToLowerInvariant();

        // Simple similarity check: Levenshtein distance or substring matching
        // For now, use a simple approach: check if outputs are very similar in length and content
        if (Math.Abs(normalized1.Length - normalized2.Length) > normalized1.Length * 0.2)
        {
            return false; // Length differs by more than 20%
        }

        // Check if one contains the other (indicating repetition)
        if (normalized1.Contains(normalized2.Substring(0, Math.Min(50, normalized2.Length))) ||
            normalized2.Contains(normalized1.Substring(0, Math.Min(50, normalized1.Length))))
        {
            return true;
        }

        return false;
    }
}
