using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using global::A2A;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;

namespace Agw.Agents.Execution.Agents.ExternalAgents.A2A;

public sealed class A2ARemoteAgentSession : AgentSession
{
    private static readonly FrozenDictionary<string, TaskState> TaskStatesByName = Enum.GetValues<TaskState>()
        .ToFrozenDictionary(FormatTaskStateName, StringComparer.OrdinalIgnoreCase);

    public A2ARemoteAgentSession() { }

    private A2ARemoteAgentSession(AgentSessionStateBag stateBag)
        : base(stateBag) { }

    public Guid? InterfaceRevision { get; private set; }
    public string? ContextId { get; private set; }
    public string? TaskId { get; private set; }
    public TaskState? TaskState { get; private set; }

    internal void BindEndpointRevision(Guid revision)
    {
        if (InterfaceRevision != revision)
        {
            ClearRemoteState();
            InterfaceRevision = revision;
        }
    }

    internal void RecordContext(string? contextId)
    {
        if (string.IsNullOrEmpty(contextId))
        {
            return;
        }
        if (ContextId is not null && ContextId != contextId)
        {
            throw new AgwException(
                ErrorCodes.A2ARemoteAgentRequestFailed,
                "The remote A2A agent changed the context ID for an existing session."
            );
        }
        ContextId = contextId;
    }

    internal void RecordTask(string taskId, TaskState state)
    {
        TaskId = taskId;
        TaskState = state;
    }

    internal void ClearTask()
    {
        TaskId = null;
        TaskState = null;
    }

    internal void ClearRemoteState()
    {
        ContextId = null;
        ClearTask();
    }

    internal JsonElement Serialize(JsonSerializerOptions? options) =>
        JsonSerializer.SerializeToElement(
            new State
            {
                InterfaceRevision = InterfaceRevision,
                ContextId = ContextId,
                TaskId = TaskId,
                TaskState = FormatTaskState(TaskState),
                StateBag = StateBag.Serialize(),
            },
            options
        );

    internal static A2ARemoteAgentSession Deserialize(JsonElement state, JsonSerializerOptions? options)
    {
        var saved =
            state.Deserialize<State>(options)
            ?? throw new AgwException(ErrorCodes.InvalidParam, "The A2A agent session is empty.");
        return new A2ARemoteAgentSession(AgentSessionStateBag.Deserialize(saved.StateBag))
        {
            InterfaceRevision = saved.InterfaceRevision,
            ContextId = saved.ContextId,
            TaskId = saved.TaskId,
            TaskState = ReadTaskState(saved.TaskState),
        };
    }

    internal static string? FormatTaskState(TaskState? state) => state is { } value ? FormatTaskStateName(value) : null;

    private static string FormatTaskStateName(TaskState state) =>
        JsonNamingPolicy.KebabCaseLower.ConvertName(state.ToString());

    private static TaskState? ReadTaskState(string? name)
    {
        if (name is null)
            return null;
        return TaskStatesByName.TryGetValue(name, out var state)
            ? state
            : throw new AgwException(ErrorCodes.InvalidParam, "The A2A agent session contains an invalid task state.");
    }

    private sealed class State
    {
        [JsonPropertyName("interfaceRevision")]
        public Guid? InterfaceRevision { get; init; }

        [JsonPropertyName("contextId")]
        public string? ContextId { get; init; }

        [JsonPropertyName("taskId")]
        public string? TaskId { get; init; }

        // 会话存储使用 kebab-case，独立于 SDK 定义的协议名称。
        // Session storage uses kebab-case independently of the SDK protocol names.
        [JsonPropertyName("taskState")]
        public string? TaskState { get; init; }

        [JsonPropertyName("stateBag")]
        public JsonElement StateBag { get; init; }
    }
}
