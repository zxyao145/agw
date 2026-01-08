using ClaudeCodeSdk;
using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using DSystem.Domain.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Net.Mail;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using static System.Net.Mime.MediaTypeNames;

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
    public async IAsyncEnumerable<AiMessage2> ExecuteStreamingAsync(
        string prompt,
        string? workingDirectory = null,
        string? apiKey = null,
        string? baseUrl = null,
        string? systemPrompt = null,
        int? maxTurns = null,
        string? sessionId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new ClaudeCodeOptions
        {
            WorkingDirectory = workingDirectory,
            SystemPrompt = systemPrompt,
            MaxTurns = maxTurns,
            Resume = sessionId
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


        var aiAgent = new ClaudeCodeAIAgent(options, _logger);
        var agentRunResponseUpdate = aiAgent.RunStreamingAsync(prompt, cancellationToken: cancellationToken);
        await foreach (var message in agentRunResponseUpdate)
        {
            // Convert SDK message to our DTO
            var claudeMessage = ConvertMessage(message);
            if (claudeMessage != null)
            {
                yield return claudeMessage;
            }
        }

        //await using var client = new ClaudeSdkClient(options, _logger);
        //await client.ConnectAsync();
        //await client.QueryAsync(prompt, cancellationToken: cancellationToken);
        //// Execute streaming query
        //await foreach (var message in client.ReceiveResponseAsync())
        //{
        //    // Convert SDK message to our DTO
        //    var claudeMessage = ConvertMessage(message);
        //    if (claudeMessage != null)
        //    {
        //        yield return claudeMessage;
        //    }
        //}
    }

    private AiMessage2? ConvertMessage(IMessage message)
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
    /// Convert AgentRunResponseUpdate to ClaudeCodeMessage DTO.
    /// </summary>
    private AiMessage2 ConvertMessage(AgentRunResponseUpdate msg)
    {
        var role = msg.Role;
        var roleStr = role.HasValue ? role.Value.Value : "";
        var contents = msg.Contents;
        var aiMsgContents = contents.Select(content =>
        {
            AiMessageContent? ac = null;
            if (content is TextContent textContent)
            {
                ac = new AiMessageContent(content.GetType().Name, textContent.Text);
            }
            else if (content is FunctionCallContent call)
            {
                //var t = (call.Arguments != null)
                //    ? (call.Name + "(" + string.Join(", ", call.Arguments) + ")")
                //    : (call.Name + "()");

                var t = $"[Tool: {call.Name}]";
                ac = new AiMessageContent(content.GetType().Name, t);
            }
            else if (content is FunctionResultContent callResult)
            {
                //var t = callResult.Exception != null
                //    ? (callResult.Exception.GetType().Name
                //        + "(\"" + callResult.Exception.Message + "\")")
                //    : ((callResult.Result?.ToString() ?? "(null)") ?? "");

                var t = $"[Tool Result: {callResult.Result}]";
                ac = new AiMessageContent(content.GetType().Name, t);
            }

            return ac;
        })
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();
        var aiMessage = new AiMessage2
            (
                msg.MessageId ?? "",
                msg.AuthorName,
                roleStr,
                aiMsgContents
            );

        return aiMessage;
    }

    /// <summary>
    /// Convert AssistantMessage to ClaudeCodeMessage.
    /// </summary>
    private AiMessage2 ConvertAssistantMessage(AssistantMessage message)
    {

        var c = ConvertContent(message.Content);
        return new AiMessage2(
            "",
            message.Model,
            ChatRole.Assistant.Value,
            [
                new AiMessageContent("TextContent", c)
                ]

            );
    }

    private static string ConvertContent(IEnumerable<IContentBlock> contents)
    {
        string content = "";
        foreach (var item in contents)
        {
            if (item is TextBlock textBlock)
            {
                content += textBlock.Text;
            }

            if (item is ThinkingBlock thinkingBlock)
            {
                content += "Thinking: " + thinkingBlock.Thinking;
            }

            if (item is ToolUseBlock toolUseBlock)
            {
                content += "Using Tool:" + toolUseBlock.Name;
            }

            if (item is ToolResultBlock toolResultBlock)
            {
                content += $"Using Result:" + toolResultBlock.Content;
            }
        }
        return content;
    }

    /// <summary>
    /// Convert ResultMessage to ClaudeCodeMessage.
    /// </summary>
    private AiMessage2 ConvertResultMessage(ResultMessage message)
    {
        //return new ClaudeCodeMessage
        //{
        //    Type = "result",
        //    Content = message.Result ?? string.Empty,
        //    NumTurns = message.NumTurns,
        //    TotalCostUsd = message.TotalCostUsd,
        //    IsError = message.IsError,
        //    ErrorMessage = message.IsError ? message.Result : null
        //};

        return new AiMessage2
        ("", "result", ChatRole.System.Value,
        [new AiMessageContent("TextContent", message.Result ?? string.Empty)]
        );

    }

    /// <summary>
    /// Convert SystemMessage to ClaudeCodeMessage.
    /// </summary>
    private AiMessage2 ConvertSystemMessage(SystemMessage message)
    {
        // Convert Data dictionary to readable string
        var dataContent = string.Join("\n",
            message.Data.Select(kvp => $"{kvp.Key}: {kvp.Value}"));

        return new AiMessage2
        ("", message.Subtype, ChatRole.System.Value,
        [ new AiMessageContent("TextContent", dataContent) ]
        );
    }

    /// <summary>
    /// Convert UserMessage to ClaudeCodeMessage.
    /// </summary>
    private AiMessage2 ConvertUserMessage(UserMessage message)
    {
        string c = "";


        // Handle Content which can be string or List<IContentBlock>
        if (message.Content is string str)
        {
            c = str;
        }
        else if (message.Content is IEnumerable<IContentBlock> blocks)
        {
            c = ConvertContent(blocks);
        }
        else
        {
            c = message.Content?.ToString() ?? string.Empty;
        }
        var ccMsg = new AiMessage2(
                 "",
                 "user",
                 ChatRole.Assistant.Value,
                 [
                     new AiMessageContent("TextContent", c)
                 ]
            );
        return ccMsg;
    }
}
