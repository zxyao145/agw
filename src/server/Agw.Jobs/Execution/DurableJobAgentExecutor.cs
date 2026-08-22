using Agw.Agents.Execution.Commands.Setting;
using Agw.Agents.Execution.Connections;
using Agw.Agents.Execution.Durable;
using Agw.Projects.Application;
using Agw.Shared;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;

namespace Agw.Jobs.Execution;

public sealed class DurableJobAgentExecutor : IJobAgentExecutor
{
    private readonly IDurableExecutionClient _executionClient;
    private readonly TaskExecutionAppService _taskExecutionAppService;

    public DurableJobAgentExecutor(
        IDurableExecutionClient executionClient,
        TaskExecutionAppService taskExecutionAppService
    )
    {
        _executionClient = executionClient;
        _taskExecutionAppService = taskExecutionAppService;
    }

    public async Task ExecuteAsync(Job job, Guid executionId, CancellationToken cancellationToken)
    {
        if (job.AgentId == null || job.AgentType == null)
        {
            throw new AgwException(ErrorCodes.JobAgentTargetRequired);
        }

        var (prompt, title) = JobAgentExecutor.BuildPromptAndTitle(job);
        var contextId = ContextIdUtil.GenContextId();
        var ownerUserId = JobAgentExecutor.ResolveOwnerUserId(job);
        var createResult = await _taskExecutionAppService.CreateRunningForExecutionAsync(
            job.ProjectId,
            executionId,
            new TaskCreateRequest(JobId: job.Id, Input: prompt, Title: title, ContextId: contextId),
            ownerUserId
        );
        if (createResult.Type != ApplicationResultType.Success || createResult.Value == null)
        {
            throw new AgwException(
                ErrorCodes.TaskCreationFailed,
                createResult.Error ?? "Failed to create task for job execution."
            );
        }

        var task =
            await _taskExecutionAppService.GetTaskAsync(executionId)
            ?? throw new AgwException(ErrorCodes.TaskCreationFailed, "Failed to resolve the created Job task.");
        var input = new AgwUserInput
        {
            MessageId = executionId.ToString("D"),
            Author = Constants.DefaultInputAuthor,
            Contents = [new AgwTextContent { Content = prompt }],
        };
        var settings = ExecutionSettings.FromCommand(new SettingCommand(job.ProjectId, contextId: contextId));
        await _executionClient.StartAsync(
            new DurableExecutionRequest(
                executionId,
                ownerUserId,
                job.AgentId.Value,
                job.AgentType.Value,
                input,
                task,
                settings
            ),
            cancellationToken
        );

        await WaitForCompletionAsync(executionId, ownerUserId, cancellationToken);
    }

    private async Task WaitForCompletionAsync(Guid executionId, string ownerUserId, CancellationToken cancellationToken)
    {
        var outcome = await _executionClient
            .WaitForActionableOutcomeAsync(executionId, ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        switch (outcome.Status)
        {
            case DurableExecutionStatus.Completed:
                return;
            case DurableExecutionStatus.Failed:
                throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    outcome.ErrorMessage ?? "The distributed Job execution failed."
                );
            case DurableExecutionStatus.Interrupted:
                throw new AgwException(ErrorCodes.AgentExecutionFailed, "The distributed Job was interrupted.");
            case DurableExecutionStatus.WaitingForHuman:
                await _executionClient.InterruptAsync(
                    executionId,
                    ownerUserId,
                    "Scheduled Jobs do not support human interaction.",
                    cancellationToken
                );
                throw new AgwException(
                    ErrorCodes.AgentExecutionFailed,
                    "Scheduled Jobs do not support human interaction."
                );
            default:
                throw new AgwException(
                    ErrorCodes.DurableExecutionConflict,
                    $"Unexpected actionable durable execution status '{outcome.Status}'."
                );
        }
    }
}
