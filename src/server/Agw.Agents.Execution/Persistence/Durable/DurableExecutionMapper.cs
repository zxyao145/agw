using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Inbound.Connections;

namespace Agw.Agents.Execution.Persistence.Durable;

internal static class DurableExecutionMapper
{
    public static DurableProjectTaskSnapshot FromProjection(AgentExecutionTask task) =>
        new()
        {
            TaskId = task.TaskId,
            Generation = task.Generation,
            ProjectConversationId = task.ProjectConversationId,
            ProjectId = task.ProjectId,
            ContextId = task.ContextId,
        };

    public static AgentExecutionTask ToProjection(this DurableProjectTaskSnapshot task) =>
        new()
        {
            TaskId = task.TaskId,
            Generation = task.Generation,
            ProjectConversationId = task.ProjectConversationId,
            ProjectId = task.ProjectId,
            ContextId = task.ContextId,
        };

    public static DurableExecutionSettings FromSettings(ExecutionSettings settings) =>
        new()
        {
            EnvironmentVariables = settings
                .EnvironmentVariables.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            PermissionMode = settings.PermissionMode,
            PermissionVersion = settings.PermissionVersion,
            HumanInteractionPolicy = settings.HumanInteractionPolicy,
            Resume = settings.Resume,
        };

    public static SettingCommand ToCommand(this DurableExecutionSettings settings, Guid projectId, string contextId) =>
        new(
            projectId,
            new Dictionary<string, string>(settings.EnvironmentVariables),
            contextId,
            settings.PermissionMode
        )
        {
            Resume = settings.Resume,
        };
}
