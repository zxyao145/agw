using Agw.Agents.Application.Persistence;
using Agw.Agents.Execution.Runtimes;

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
            ResultOnly = settings.ResultOnly,
            Resume = settings.Resume,
        };

    public static ExecutionSettings ToRuntimeSettings(
        this DurableExecutionSettings settings,
        Guid projectId,
        string contextId
    ) =>
        new(
            projectId,
            contextId,
            settings.EnvironmentVariables,
            settings.PermissionMode,
            settings.Resume,
            settings.HumanInteractionPolicy,
            settings.PermissionVersion,
            settings.ResultOnly
        );
}
