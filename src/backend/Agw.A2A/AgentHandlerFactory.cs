using System.Collections.Concurrent;
using System.Text.Json;

using A2A;

using Agw.Shared.Exceptions;
using Agw.Shared.Models;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.A2A;

public class CommonAgentHandler : IAgentHandler
{
    private AgentCard? _agentCard;
    private readonly string _agentName;
    private readonly IAgentExecutionBridge _executionBridge;
    private readonly A2AAgentService? _a2aAgentService;
    private readonly IServiceScopeFactory? _serviceScopeFactory;

    public CommonAgentHandler(
        string agentName,
        IAgentExecutionBridge executionBridge,
        A2AAgentService a2aAgentService)
    {
        _agentName = agentName;
        _executionBridge = executionBridge;
        _a2aAgentService = a2aAgentService;
    }

    public CommonAgentHandler(
        string agentName,
        IAgentExecutionBridge executionBridge,
        IServiceScopeFactory serviceScopeFactory)
    {
        _agentName = agentName;
        _executionBridge = executionBridge;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public async Task<AgentCard?> GetAgentCardAsync()
    {
        if (_agentCard is not null)
        {
            return _agentCard;
        }

        if (_a2aAgentService is not null)
        {
            _agentCard = await _a2aAgentService.GetAgentCardAsync(_agentName).ConfigureAwait(false);
            return _agentCard;
        }

        ArgumentNullException.ThrowIfNull(_serviceScopeFactory);

        using var scope = _serviceScopeFactory.CreateScope();
        var scopedA2AAgentService = scope.ServiceProvider.GetRequiredService<A2AAgentService>();
        _agentCard = await scopedA2AAgentService.GetAgentCardAsync(_agentName).ConfigureAwait(false);
        return _agentCard;
    }

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.SubmitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var input = ToAgwUserInput(context);
            if (context.StreamingResponse)
            {
                await updater.StartWorkAsync(CreateStatusMessage(context, "Working"), cancellationToken).ConfigureAwait(false);

                await foreach (var message in _executionBridge
                                   .ExecuteStreamingAsync(_agentName, context, input, cancellationToken)
                                   .ConfigureAwait(false))
                {
                    await PublishArtifactsAsync(updater, message, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                var result = await _executionBridge
                    .ExecuteAsync(_agentName, context, input, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    throw new AgwException(ErrorCodes.AgentReturnedNoResult, $"Agent '{_agentName}' returned no result.");
                }

                foreach (var message in result.Messages)
                {
                    await PublishArtifactsAsync(updater, message, cancellationToken).ConfigureAwait(false);
                }
            }

            await updater.CompleteAsync(CreateStatusMessage(context, "Completed"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await updater.FailAsync(CreateStatusMessage(context, ex.Message), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        await new TaskUpdater(eventQueue, context.TaskId, context.ContextId).CancelAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AgwUserInput ToAgwUserInput(RequestContext context)
    {
        var contents = context.Message.Parts
            .Select(ConvertPart)
            .OfType<AgwContent>()
            .ToList();
        if (contents.Count == 0)
        {
            contents.Add(new AgwTextContent { Content = context.UserText ?? string.Empty });
        }

        return new AgwUserInput
        {
            MessageId = string.IsNullOrWhiteSpace(context.Message.MessageId)
                ? Guid.NewGuid().ToString("N")
                : context.Message.MessageId,
            Author = Constants.DefaultAuthor,
            Contents = contents
        };
    }

    private static AgwContent? ConvertPart(Part part)
    {
        var type = part.ContentCase;
        switch (type)
        {
            case PartContentCase.Text:
                return new AgwTextContent { Content = part.Text };

            case PartContentCase.Url:
                return new AgwUriContent(part.Url!, string.IsNullOrWhiteSpace(part.MediaType) ? "text/plain" : part.MediaType);

            case PartContentCase.Raw:
                return new AgwTextContent { Content = Convert.ToBase64String(part.Raw!) };

            case PartContentCase.Data:
                return new AgwTextContent { Content = part.Data!.Value.GetRawText() };

            default:
                return null;
        }
    }

    private static async Task PublishArtifactsAsync(
        TaskUpdater updater,
        AgwMessage message,
        CancellationToken cancellationToken)
    {
        if (IsTurnFinished(message))
        {
            return;
        }

        var parts = ConvertMessageToParts(message);
        if (parts.Count == 0)
        {
            return;
        }

        await updater.AddArtifactAsync(
            parts,
            artifactId: string.IsNullOrWhiteSpace(message.MessageId) ? null : message.MessageId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static List<Part> ConvertMessageToParts(AgwMessage message)
    {
        var parts = new List<Part>();
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case AgwTextContent textContent when !string.IsNullOrWhiteSpace(textContent.Content):
                    parts.Add(Part.FromText(textContent.Content));
                    break;

                case AgwTextReasoningContent reasoningContent when !string.IsNullOrWhiteSpace(reasoningContent.Content):
                    parts.Add(Part.FromText(reasoningContent.Content));
                    break;

                case AgwFunctionCallContent functionCallContent when !string.IsNullOrWhiteSpace(functionCallContent.Content):
                    parts.Add(Part.FromText(functionCallContent.Content));
                    break;

                case AgwFunctionResultContent functionResultContent when !string.IsNullOrWhiteSpace(functionResultContent.Content):
                    parts.Add(Part.FromText(functionResultContent.Content));
                    break;

                case AgwErrorContent errorContent when !string.IsNullOrWhiteSpace(errorContent.Content):
                    parts.Add(Part.FromText(errorContent.Content));
                    break;

                case AgwUsageContent usageContent:
                    parts.Add(Part.FromText(JsonSerializer.Serialize(usageContent.Content)));
                    break;

                case AgwUriContent uriContent:
                    parts.Add(Part.FromUrl(uriContent.Uri.ToString(), uriContent.MediaType, null));
                    break;

                case AgwDataContent dataContent:
                    parts.Add(Part.FromUrl(dataContent.Uri, dataContent.MediaType, null));
                    break;
            }
        }

        return parts;
    }

    private static bool IsTurnFinished(AgwMessage message) =>
        message.Contents
            .OfType<AgwTextContent>()
            .Any(content =>
                content.AdditionalProperties?.TryGetValue("type", out var value) == true
                && string.Equals(value?.ToString(), "turn-finished", StringComparison.OrdinalIgnoreCase));

    private static Message CreateStatusMessage(RequestContext context, string text) => new()
    {
        Role = Role.Agent,
        MessageId = Guid.NewGuid().ToString("N"),
        ContextId = context.ContextId,
        TaskId = context.TaskId,
        Parts = [Part.FromText(text)]
    };
}

public class AgentHandlerFactory
{
    private readonly ConcurrentDictionary<string, Lazy<Task<IAgentHandler?>>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IServiceScopeFactory? _serviceScopeFactory;
    private readonly A2AAgentService? _a2aAgentService;
    private readonly IAgentExecutionBridge _executionBridge;

    [ActivatorUtilitiesConstructor]
    public AgentHandlerFactory(IServiceScopeFactory serviceScopeFactory, IAgentExecutionBridge executionBridge)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _executionBridge = executionBridge;
    }

    /// <summary>
    /// display for test
    /// </summary>
    /// <param name="a2aAgentService"></param>
    /// <param name="executionBridge"></param>
    public AgentHandlerFactory(A2AAgentService a2aAgentService, IAgentExecutionBridge executionBridge)
    {
        _a2aAgentService = a2aAgentService;
        _executionBridge = executionBridge;
    }

    public async Task<IAgentHandler?> CreateAsync(string agentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);

        var lazyHandler = _handlers.GetOrAdd(
            agentName,
            key => new Lazy<Task<IAgentHandler?>>(
                () => CreateHandlerAsync(key),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var handler = await lazyHandler.Value.ConfigureAwait(false);
            if (handler is null)
            {
                _handlers.TryRemove(agentName, out _);
            }

            return handler;
        }
        catch
        {
            _handlers.TryRemove(agentName, out _);
            throw;
        }
    }

    private async Task<IAgentHandler?> CreateHandlerAsync(string agentName)
    {
        CommonAgentHandler? handler = null;
        if (_a2aAgentService is not null)
        {
            handler = new CommonAgentHandler(agentName, _executionBridge, _a2aAgentService);
        }
        else if (_serviceScopeFactory is not null)
        {
            handler = new CommonAgentHandler(agentName, _executionBridge, _serviceScopeFactory);
        }
        if (handler is null)
        {
            return null;
        }

        var agentCard = await handler.GetAgentCardAsync().ConfigureAwait(false);
        return agentCard is null ? null : handler;
    }
}
