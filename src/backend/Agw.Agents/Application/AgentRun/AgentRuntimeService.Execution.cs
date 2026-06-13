using System.Runtime.CompilerServices;

using Agw.Agents.Application.AgentRun.Dtos;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Agw.Shared.Extensions;

using Microsoft.Agents.AI;

using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.Agents.Application.AgentRun;

public partial class AgentRuntimeService
{
    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        AgentExecSession session,
        AgwUserInput input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            await foreach (var message in session.ExecuteStreamingAsync(input, cancellationToken).ConfigureAwait(false))
            {
                yield return message;
            }

            yield return CreateTurnFinishedMessage(cancellationToken);
        }
        finally
        {
            await _sessionStateStore.SaveAsync(session._taskId, session.Agent, session.Session, cancellationToken);
        }
    }

    public async Task<AgentExecutionResult?> ExecuteByNameAsync(
        AgentExecuteByNameRequest request,
        CancellationToken cancellationToken = default)
    {
        var agent = await _agentAppService.GetAgentByNameAsync(request.AgentName);
        if (agent == null)
        {
            return null;
        }

        var req = new AgentExecuteRequest
        {
            Agent = agent,
            Input = request.Input,
            TaskId = request.TaskId,
            ProjectId = request.ProjectId,
            ContextId = request.ContextId,
        };

        return await ExecuteAsync(req, cancellationToken);
    }

    public async Task<AgentExecutionResult?> ExecuteByIdAsync(
        AgentExecuteByIdRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = await _agentAppService.GetAgentAsync(request.AgentId);
        if (agent == null)
        {
            return null;
        }

        var req = new AgentExecuteRequest
        {
            Agent = agent,
            Input = request.Input,
            TaskId = request.TaskId,
            ProjectId = request.ProjectId,
            ContextId = request.ContextId,
        };
        return await ExecuteAsync(req, cancellationToken);
    }

    private async Task<AgentExecutionResult?> ExecuteAsync(
        AgentExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        Guid? taskId = request.TaskId;
        List<ChatMessage> chatMsg = request.Input;
        Guid? projectId = request.ProjectId;
        string? contextId = request.ContextId;
        Agent agent = request.Agent;

        projectId = ProjectDefaults.GetDefaultProjectIdentifier(projectId);
        var aiAgent = await CreateAiAgentAsync(new CreateAiAgentRequest
        {
            Agent = agent,
            ProjectId = projectId
        }, cancellationToken);
        if (aiAgent == null)
        {
            throw new AgwException(ErrorCodes.AiAgentCreationFailed);
        }

        try
        {
            taskId ??= Guid.NewGuid();
            string taskIdValue = taskId.Value.Normalize();
            var session = await _sessionStateStore.GetOrCreateAsync(agent, aiAgent, taskIdValue, cancellationToken)
                .ConfigureAwait(false);

            _providerSessionState.InitializeSessionState(
                session,
                string.IsNullOrWhiteSpace(contextId) ? taskIdValue : contextId,
                taskIdValue,
                ProjectDefaults.GetDefaultProjectIdentifier(projectId));

            var messages = await CollectStreamingMessagesAsync(aiAgent, chatMsg, session).ConfigureAwait(false);

            return new AgentExecutionResult(taskIdValue, messages);
        }
        finally
        {
            if (aiAgent is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else if (aiAgent is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static async Task<List<AgwMessage>> CollectStreamingMessagesAsync(
        AIAgent aiAgent,
        IReadOnlyList<ChatMessage> chatMessages,
        AgentSession session)
    {
        var stream = aiAgent.RunStreamingAsync(chatMessages, session);
        var messages = new List<AgwMessage>();
        await foreach (var update in stream)
        {
            var msg = update.ToAiMessage();
            if (msg != null)
            {
                messages.Add(msg);
            }
        }

        return messages;
    }
}
