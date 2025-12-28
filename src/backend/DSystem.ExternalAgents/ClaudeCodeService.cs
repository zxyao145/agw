using ClaudeCodeSdk;
using ClaudeCodeSdk.Types;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace DSystem.ExternalAgents;

/// <summary>
/// Message from ClaudeCode execution for SSE streaming.
/// </summary>
public record ClaudeCodeMessage
{
    public string Type { get; init; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Model { get; init; }
    public int? NumTurns { get; init; }
    public double? TotalCostUsd { get; init; }
    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Service for executing ClaudeCode queries with streaming support.
/// </summary>
public class ClaudeCodeService
{
    private readonly ILogger<ClaudeCodeService> _logger;

    public ClaudeCodeService(ILogger<ClaudeCodeService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Execute ClaudeCode query with streaming responses.
    /// </summary>
    /// <param name="prompt">User prompt to send to ClaudeCode</param>
    /// <param name="workingDirectory">Working directory for ClaudeCode (optional)</param>
    /// <param name="apiKey">Anthropic API key (optional, uses environment variable if not provided)</param>
    /// <param name="baseUrl">Anthropic base URL (optional)</param>
    /// <param name="systemPrompt">System prompt for ClaudeCode (optional)</param>
    /// <param name="maxTurns">Maximum number of turns (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of ClaudeCodeMessage</returns>
    public async IAsyncEnumerable<ClaudeCodeMessage> ExecuteStreamingAsync(
        string prompt,
        string? workingDirectory = null,
        string? apiKey = null,
        string? baseUrl = null,
        string? systemPrompt = null,
        int? maxTurns = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new ClaudeCodeOptions
        {
            WorkingDirectory = workingDirectory,
            SystemPrompt = systemPrompt,
            MaxTurns = maxTurns
        };
        options.EnvironmentVariables = new Dictionary<string, string?>()
        {
            //{"ANTHROPIC_AUTH_TOKEN", apiKey },
            {"ANTHROPIC_BASE_URL", "https://api.deepseek.com/anthropic" },
        };

        // Set API key and base URL if provided
        if (!string.IsNullOrEmpty(apiKey))
        {
            options.ApiKey = apiKey;
        }

        if (!string.IsNullOrEmpty(baseUrl))
        {
            options.BaseUrl = baseUrl;
        }
        await using var client = new ClaudeSdkClient(options, _logger);

        await client.ConnectAsync();
        await client.QueryAsync(prompt, cancellationToken: cancellationToken);
        // Execute streaming query
        await foreach (var message in client.ReceiveResponseAsync())
        {
            // Convert SDK message to our DTO
            var claudeMessage = ConvertMessage(message);
            if (claudeMessage != null)
            {
                yield return claudeMessage;
            }
        }
    }

    /// <summary>
    /// Convert SDK IMessage to ClaudeCodeMessage DTO.
    /// </summary>
    private ClaudeCodeMessage? ConvertMessage(IMessage message)
    {
        return message switch
        {
            AssistantMessage assistantMessage => ConvertAssistantMessage(assistantMessage),
            ResultMessage resultMessage => ConvertResultMessage(resultMessage),
            SystemMessage systemMessage => ConvertSystemMessage(systemMessage),
            UserMessage userMessage => ConvertUserMessage(userMessage),
            _ => null
        };
    }

    /// <summary>
    /// Convert AssistantMessage to ClaudeCodeMessage.
    /// </summary>
    private ClaudeCodeMessage ConvertAssistantMessage(AssistantMessage message)
    {
        var ccMsg = new ClaudeCodeMessage
        {
            Type = "assistant",
            Content = "",
            Model = message.Model
        };

        ConvertContent(message.Content, ccMsg);
        return ccMsg;
    }

    private static void ConvertContent(IEnumerable<IContentBlock> contents, ClaudeCodeMessage ccMsg)
    {
        foreach (var item in contents)
        {
            if (item is TextBlock textBlock)
            {
                ccMsg.Content += textBlock.Text;
            }

            if (item is ThinkingBlock thinkingBlock)
            {
                ccMsg.Content += "Thinking: " + thinkingBlock.Thinking;
            }

            if (item is ToolUseBlock toolUseBlock)
            {
                ccMsg.Content += "Using Tool:" + toolUseBlock.Name;
            }

            if (item is ToolResultBlock toolResultBlock)
            {
                ccMsg.Content += $"Using Result:" + toolResultBlock.Content;
            }
        }
    }

    /// <summary>
    /// Convert ResultMessage to ClaudeCodeMessage.
    /// </summary>
    private ClaudeCodeMessage ConvertResultMessage(ResultMessage message)
    {
        return new ClaudeCodeMessage
        {
            Type = "result",
            Content = message.Result ?? string.Empty,
            NumTurns = message.NumTurns,
            TotalCostUsd = message.TotalCostUsd,
            IsError = message.IsError,
            ErrorMessage = message.IsError ? message.Result : null
        };
    }

    /// <summary>
    /// Convert SystemMessage to ClaudeCodeMessage.
    /// </summary>
    private ClaudeCodeMessage ConvertSystemMessage(SystemMessage message)
    {
        // Convert Data dictionary to readable string
        var dataContent = string.Join("\n",
            message.Data.Select(kvp => $"{kvp.Key}: {kvp.Value}"));

        return new ClaudeCodeMessage
        {
            Type = "system",
            Content = $"[{message.Subtype}]\n{dataContent}"
        };
    }

    /// <summary>
    /// Convert UserMessage to ClaudeCodeMessage.
    /// </summary>
    private ClaudeCodeMessage ConvertUserMessage(UserMessage message)
    {
        var ccMsg = new ClaudeCodeMessage
        {
            Type = "user",
            Content = ""
        };

        // Handle Content which can be string or List<IContentBlock>
        if (message.Content is string str)
        {
            ccMsg.Content = str;
        }
        else if (message.Content is IEnumerable<IContentBlock> blocks)
        {
            ConvertContent(blocks, ccMsg);
        }
        else
        {
            ccMsg.Content = message.Content?.ToString() ?? string.Empty;
        }

        return ccMsg;
    }
}
