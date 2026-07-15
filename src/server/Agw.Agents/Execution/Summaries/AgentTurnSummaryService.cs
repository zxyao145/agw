using Agw.Shared.Contracts.Projects;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.Execution.Summaries;

public sealed class AgentTurnSummaryService : IAgentTurnSummaryService
{
    internal const string FailureText = "Summary generation failed.";
    private const string SummaryAgentName = "$summary";

    private const string DefaultInstructions =
        "You summarize one completed agent turn. Use the same language as the user's input when identifiable. " +
        "Concisely capture the user's request, the agent's answer or actions, the outcome, and any unresolved items. " +
        "Return only the summary text. Use Markdown when it improves readability. Plain text is also acceptable. " +
        "Do not return JSON, XML, wrapper objects, or transport metadata.";

    private readonly ISummaryChatClientFactory _chatClientFactory;
    private readonly IConversationHistoryWriter _conversationHistoryWriter;
    private readonly IAgentUsageRecorder _usageRecorder;
    private readonly ILogger<AgentTurnSummaryService> _logger;

    public AgentTurnSummaryService(
        ISummaryChatClientFactory chatClientFactory,
        IConversationHistoryWriter conversationHistoryWriter,
        IAgentUsageRecorder usageRecorder,
        ILogger<AgentTurnSummaryService> logger)
    {
        _chatClientFactory = chatClientFactory;
        _conversationHistoryWriter = conversationHistoryWriter;
        _usageRecorder = usageRecorder;
        _logger = logger;
    }

    public async Task<ChatMessage> CreateResultAsync(
        Guid modelProviderId,
        IReadOnlyList<ChatMessage> sourceMessages,
        Guid projectId,
        string contextId,
        string? customInstructions,
        CancellationToken cancellationToken = default)
    {
        string resultText;
        try
        {
            using var chatClient = await _chatClientFactory
                .CreateAsync(modelProviderId, cancellationToken)
                .ConfigureAwait(false);
            if (chatClient == null)
            {
                resultText = FailureText;
            }
            else
            {
                var response = await chatClient.GetResponseAsync(
                    CreatePromptMessages(sourceMessages, customInstructions),
                    cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (response.Usage != null)
                {
                    await RecordUsageAsync(projectId, contextId, response.Usage, cancellationToken)
                        .ConfigureAwait(false);
                }

                resultText = ExtractText(response);
                if (string.IsNullOrWhiteSpace(resultText))
                {
                    resultText = FailureText;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to generate summary for project {ProjectId} and context {ContextId}.",
                projectId,
                contextId);
            resultText = FailureText;
        }

        var result = CreateResultMessage(resultText);
        await _conversationHistoryWriter.AppendAsync(
            projectId,
            contextId,
            [result],
            cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    internal static ChatMessage CreateResultMessage(string text) =>
        new(ChatRole.System, [new TextContent(text)])
        {
            MessageId = Guid.NewGuid().ToString(),
            AuthorName = Constants.DefaultAgentAuthor,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["type"] = "result"
            }
        };

    private static IReadOnlyList<ChatMessage> CreatePromptMessages(
        IReadOnlyList<ChatMessage> sourceMessages,
        string? customInstructions)
    {
        var instructions = string.IsNullOrWhiteSpace(customInstructions)
            ? DefaultInstructions
            : $"{DefaultInstructions}{Environment.NewLine}{Environment.NewLine}Additional requirements:{Environment.NewLine}{customInstructions.Trim()}";
        var transcript = string.Join(
            Environment.NewLine,
            sourceMessages.Select(FormatSourceMessage).Where(text => text != null));

        return
        [
            new ChatMessage(ChatRole.System, instructions),
            new ChatMessage(ChatRole.User, transcript)
        ];
    }

    private static string? FormatSourceMessage(ChatMessage message)
    {
        var text = string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text)).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var label = string.IsNullOrWhiteSpace(message.AuthorName)
            ? message.Role.Value
            : $"{message.Role.Value} ({message.AuthorName})";
        return $"{label}: {text}";
    }

    private static string ExtractText(ChatResponse response) =>
        string.Concat(
                response.Messages
                    .SelectMany(message => message.Contents)
                    .OfType<TextContent>()
                    .Select(content => content.Text))
            .Trim();

    private async Task RecordUsageAsync(
        Guid projectId,
        string contextId,
        UsageDetails usage,
        CancellationToken cancellationToken)
    {
        try
        {
            await _usageRecorder.AddAsync(
                projectId,
                contextId,
                SummaryAgentName,
                new ProjectContextUsage
                {
                    InputTokenCount = usage.InputTokenCount ?? 0,
                    OutputTokenCount = usage.OutputTokenCount ?? 0,
                    TotalTokenCount = usage.TotalTokenCount ?? 0,
                    CachedInputTokenCount = usage.CachedInputTokenCount ?? 0,
                    ReasoningTokenCount = usage.ReasoningTokenCount ?? 0
                },
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to record summary usage for project {ProjectId} and context {ContextId}.",
                projectId,
                contextId);
        }
    }
}
