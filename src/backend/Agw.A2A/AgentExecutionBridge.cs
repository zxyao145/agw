using A2A;
using Agw.Api.Contracts;
using Agw.Appliaction.Services.Agents;
using Agw.Domain.Entities;
using Agw.Shared;
using Agw.Shared.Abstractions.Repositories;
using Agw.Shared.Models;
using Agw.Shared.Tasks;
using Agw.Shared.Tasks.Entities;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Agw.A2A;

public interface IAgentExecutionBridge
{
    Task<AgentExecutionResult?> ExecuteAsync(
        string agentName,
        RequestContext context,
        AgwUserInput input,
        CancellationToken cancellationToken);

    IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        string agentName,
        RequestContext context,
        AgwUserInput input,
        CancellationToken cancellationToken);
}

public sealed class AgentExecutionBridge(IServiceScopeFactory serviceScopeFactory) : IAgentExecutionBridge
{
    public async Task<AgentExecutionResult?> ExecuteAsync(
        string agentName,
        RequestContext context,
        AgwUserInput input,
        CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var agentRepository = scope.ServiceProvider.GetRequiredService<IRepository<Agent>>();
        var agentRuntimeService = scope.ServiceProvider.GetRequiredService<AgentRuntimeService>();

        var agent = await agentRepository
            .SingleOrDefaultAsync(a => a.Name == agentName, cancellationToken)
            .ConfigureAwait(false);
        if (agent is null)
        {
            return null;
        }

        return await agentRuntimeService
            .ExecuteAsync(
                agent.Id,
                ParseRequiredTaskId(context.TaskId),
                [CreateChatMessage(input)],
                cancellationToken,
                projectId: ProjectDefaults.A2AId,
                contextId: context.ContextId)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<AgwMessage> ExecuteStreamingAsync(
        string agentName,
        RequestContext context,
        AgwUserInput input,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var agentRepository = scope.ServiceProvider.GetRequiredService<IRepository<Agent>>();
        var agentRuntimeService = scope.ServiceProvider.GetRequiredService<AgentRuntimeService>();

        var agent = await agentRepository
            .SingleOrDefaultAsync(a => a.Name == agentName, cancellationToken)
            .ConfigureAwait(false);
        if (agent is null)
        {
            throw new InvalidOperationException($"Agent '{agentName}' not found.");
        }

        var taskId = ParseRequiredTaskId(context.TaskId);
        var projectTask = new ProjectTask
        {
            Id = taskId,
            ProjectId = ProjectDefaults.A2AId,
            ContextId = context.ContextId,
            Title = agent.Name,
            CreateBy = Constants.DefaultAuthor,
            CreateTime = DateTime.UtcNow
        };
        var settings = new SettingCommand(ProjectDefaults.A2AId, taskId)
        {
            Resume = context.IsContinuation
        };

        await using var session = await agentRuntimeService
            .CreateSessionAsync(agent.Id, projectTask, settings, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
        {
            throw new InvalidOperationException($"Unable to create session for agent '{agentName}'.");
        }

        await foreach (var message in agentRuntimeService
                           .ExecuteStreamingAsync(session, input, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return message;
        }
    }

    private static Guid ParseRequiredTaskId(string taskId)
    {
        if (!Guid.TryParse(taskId, out var taskGuid))
        {
            throw new ArgumentException("A2A task id must be a GUID string.", nameof(taskId));
        }

        return taskGuid;
    }

    private static ChatMessage CreateChatMessage(AgwUserInput input)
    {
        var message = new ChatMessage(ChatRole.User, ConvertToAIContents(input.Contents))
        {
            MessageId = input.MessageId,
            AuthorName = string.IsNullOrWhiteSpace(input.Author) ? Constants.DefaultAuthor : input.Author
        };

        return message;
    }

    private static List<AIContent> ConvertToAIContents(List<AgwContent> contents)
    {
        var aiContents = new List<AIContent>();

        foreach (var item in contents)
        {
            switch (item)
            {
                case AgwTextContent textContent:
                    aiContents.Add(new TextContent(textContent.Content));
                    break;
                case AgwTextReasoningContent reasoningContent:
                    aiContents.Add(new TextContent(reasoningContent.Content));
                    break;
                case AgwUriContent uriContent:
                    aiContents.Add(new UriContent(uriContent.Uri, uriContent.MediaType));
                    break;
                case AgwFunctionCallContent functionCallContent:
                    aiContents.Add(new TextContent(functionCallContent.Content));
                    break;
                case AgwFunctionResultContent functionResultContent:
                    aiContents.Add(new TextContent(functionResultContent.Content));
                    break;
                case AgwErrorContent errorContent:
                    aiContents.Add(new TextContent(errorContent.Content));
                    break;
                case AgwUsageContent usageContent:
                    aiContents.Add(new TextContent(System.Text.Json.JsonSerializer.Serialize(usageContent.Content)));
                    break;
            }
        }

        return aiContents;
    }
}
