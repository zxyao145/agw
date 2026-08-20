using Agw.Shared.Data.Entities.Agents;

namespace Agw.Agents.Execution.Agents.Dtos;

public sealed class CreateAiAgentRequest
{
    public Guid? ProjectId { get; init; }

    public Guid ConversationId { get; init; }

    /// <summary>
    /// external agent session id
    /// </summary>
    public Guid? ProviderSessionId { get; init; }

    public required Agent Agent { get; init; }

    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    public Func<string, CancellationToken, ValueTask>? OnExternalSessionStartedAsync { get; init; }

    public bool IsResume { get; init; }

    public string DefaultMode { get; init; } = "execute";

    /// <summary>
    /// 获取是否把人机交互 Tool 包装为可由 durable runtime checkpoint 的 approval 请求。
    /// </summary>
    public bool DeferHumanInteractions { get; init; }
}
