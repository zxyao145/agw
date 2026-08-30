using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Projects.Contracts.Execution;
using Agw.Shared;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;

namespace Agw.Jobs.Execution;

public sealed class JobAgentExecutor : IJobAgentExecutor
{
    private readonly IAgentExecutionFacade _agentExecutions;
    private readonly IProjectTaskFacade _projectTasks;

    public JobAgentExecutor(IAgentExecutionFacade agentExecutions, IProjectTaskFacade projectTasks)
    {
        _agentExecutions = agentExecutions;
        _projectTasks = projectTasks;
    }

    public async Task ExecuteAsync(Job job, Guid executionId, CancellationToken cancellationToken)
    {
        if (job.AgentId == null || job.AgentType == null)
        {
            throw new AgwException(ErrorCodes.JobAgentTargetRequired);
        }

        var (prompt, title) = BuildPromptAndTitle(job);
        var contextId = ContextIdUtil.GenContextId();
        var ownerUserId = ResolveOwnerUserId(job);
        using var userScope = UserInfoUtil.Push(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, ownerUserId)], "ScheduledJob"))
        );
        var task = await _projectTasks
            .GetOrCreateAsync(
                new StartProjectTaskRequest(
                    job.ProjectId,
                    executionId,
                    job.Id,
                    prompt,
                    title,
                    contextId,
                    ownerUserId,
                    ProjectTaskStatus.Running
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
        var input = new AgwUserInput
        {
            MessageId = executionId.ToString("D"),
            Author = Constants.DefaultInputAuthor,
            Contents = [new AgwTextContent { Content = prompt }],
        };
        _ = await _agentExecutions
            .ExecuteAsync(
                new AgentExecutionRequest(
                    executionId,
                    ownerUserId,
                    new AgentTarget(Map(job.AgentType.Value), job.AgentId.Value),
                    task,
                    input,
                    HumanInteractionPolicy: HumanInteractionPolicy.Reject
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    internal static (string Prompt, string Title) BuildPromptAndTitle(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var trimmedName = job.Name.Trim();
        var trimmedPrompt = job.Prompt?.Trim();

        if (!string.IsNullOrWhiteSpace(trimmedPrompt))
        {
            var title = !string.IsNullOrWhiteSpace(trimmedName)
                ? trimmedName
                : CreateTitle(trimmedPrompt, "Scheduled Job");

            return (trimmedPrompt, title);
        }

        if (!string.IsNullOrWhiteSpace(trimmedName))
        {
            return ($"Run job: {trimmedName}", trimmedName);
        }

        return ("Scheduled Job", "Scheduled Job");
    }

    internal static string ResolveOwnerUserId(Job job) =>
        TryResolveOwnerUserId(job, out var ownerUserId)
            ? ownerUserId
            : throw new AgwException(ErrorCodes.AuthenticationRequired);

    internal static bool TryResolveOwnerUserId(Job job, out string ownerUserId)
    {
        ownerUserId = job.CreateBy?.Trim() ?? string.Empty;
        return ownerUserId.Length > 0;
    }

    private static AgentTargetKind Map(AgentRuntimeType type) =>
        type switch
        {
            AgentRuntimeType.Agent => AgentTargetKind.Agent,
            AgentRuntimeType.Agentflow => AgentTargetKind.Agentflow,
            _ => throw new AgwException(ErrorCodes.UnsupportedAgentType, $"Unsupported agent type: {type}"),
        };

    private static string CreateTitle(string? text, string fallback)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed[..Math.Min(trimmed.Length, 80)];
    }
}
