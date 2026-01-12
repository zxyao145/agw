using ClaudeCodeSdk.MAF;
using ClaudeCodeSdk.Types;
using DSystem.Domain.Models;
using DSystem.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DSystem.ExternalAgents;

/// <summary>
/// Service for executing ClaudeCode queries with streaming support.
/// </summary>
public class ClaudeCodeService
{
    private readonly ILogger<ClaudeCodeService> _logger;
    private readonly HybridCache _cache;

    public ClaudeCodeService(ILogger<ClaudeCodeService> logger, HybridCache cache)
    {
        _logger = logger;
        _cache = cache;
    }

    /// <summary>
    /// Execute ClaudeCode query with streaming responses.
    /// </summary>
    /// <param name="prompt">User prompt to send to ClaudeCode</param>
    /// <param name="workingDirectory">Working directory for ClaudeCode (optional)</param>
    /// <param name="apiKey">Anthropic API key (optional, uses environment variable if not provided)</param>
    /// <param name="apiBaseUrl">Anthropic base URL (optional)</param>
    /// <param name="systemPrompt">System prompt for ClaudeCode (optional)</param>
    /// <param name="maxTurns">Maximum number of turns (optional)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Async enumerable of ClaudeCodeMessage</returns>
    public async IAsyncEnumerable<AiMessage> ExecuteStreamingAsync(
        ClaudeCodeExecuteRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var threadId = request.ThreadId;
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId, nameof(threadId));
        string prompt = request.Input;
        string? workingDirectory = request.WorkingDirectory;
        string? apiKey = request.ApiKey;
        string? apiBaseUrl = request.ApiBaseUrl;
        string? systemPrompt = request.SystemPrompt;
        int? maxTurns = request.MaxTurns;
        PermissionMode? mode = null;
        if (!string.IsNullOrWhiteSpace(request.PermissionMode))
        {
            mode = Enum.Parse<PermissionMode>(request.PermissionMode);
        }
        var options = new ClaudeCodeAIAgentOptions
        {
            WorkingDirectory = workingDirectory,
            SystemPrompt = systemPrompt,
            MaxTurns = maxTurns,
            PermissionMode = mode,
        };
        options.EnvironmentVariables = new Dictionary<string, string?>()
        {
            //{"ANTHROPIC_AUTH_TOKEN", apiKey },
            {"ANTHROPIC_BASE_URL", "https://api.deepseek.com/anthropic" },
        };

        if (!string.IsNullOrEmpty(apiKey))
        {
            options.ApiKey = apiKey;
        }
        if (!string.IsNullOrEmpty(apiBaseUrl))
        {
            options.BaseUrl = apiBaseUrl;
        }

        var aiAgent = new ClaudeCodeAIAgent(options, _logger);

        AgentThread agentThread;
        var value = await _cache.GetOrCreateAsync<string>(threadId, (c) =>
        {
            return ValueTask.FromResult("");
        });
        if (string.IsNullOrWhiteSpace(value))
        {
            agentThread = aiAgent.GetNewThread();
        }
        else
        {
            var serializedThread = JsonSerializer.Deserialize<JsonElement>(value);
            agentThread = aiAgent.DeserializeThread(serializedThread);
        }


        var agentRunResponseUpdate = aiAgent
            .RunStreamingAsync(prompt, agentThread, cancellationToken: cancellationToken);

        await foreach (var message in agentRunResponseUpdate)
        {
            // Convert SDK message to our DTO
            var aiMessage = ConvertClaudeMessage(message);
            if (aiMessage != null)
            {
                yield return aiMessage;
            }
        }

        // Save thread state to cache after execution
        var serializeJsonElement = agentThread.Serialize();
        if(serializeJsonElement.ValueKind != JsonValueKind.Undefined 
            && serializeJsonElement.ValueKind == JsonValueKind.Null)
        {
            var serialized = JsonSerializer.Serialize(serializeJsonElement);
            await _cache.SetAsync(threadId, serialized, cancellationToken: cancellationToken);
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

    private AiMessage? ConvertMessage(IMessage message)
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
    private AiMessage ConvertClaudeMessage(AgentRunResponseUpdate update)
    {
        var role = update.Role;
        var roleStr = role.HasValue ? role.Value.Value : "";
        var contents = update.Contents;


        var aiMsgContents = contents.Select(content =>
        {
            var contentAadditionalProperties = content.AdditionalProperties ?? new AdditionalPropertiesDictionary();

            AiMessageContent? aiMsgContent = null;
            if (content is TextContent textContent)
            {
                aiMsgContent = new AiMessageContent(content.GetType().Name, textContent.Text, content.AdditionalProperties);
            }
            else if (content is FunctionCallContent call)
            {
                contentAadditionalProperties.Add("callId", call.CallId);
                aiMsgContent = new AiMessageContent(content.GetType().Name, call.Name, contentAadditionalProperties);
            }
            else if (content is FunctionResultContent callResult)
            {
                var callResultContent = callResult.Result == null
                    ? ""
                    : JsonUtil.Serialize(callResult.Result);
                contentAadditionalProperties.Add("callId", callResult.CallId); 
                aiMsgContent = new AiMessageContent(content.GetType().Name, callResultContent, contentAadditionalProperties);
            }
            else if (content is TextReasoningContent thinkingContent)
            {
                var t = thinkingContent.Text;
                aiMsgContent = new AiMessageContent(content.GetType().Name, t, content.AdditionalProperties);
            }
            else if(content is ErrorContent error)
            {
                aiMsgContent = new AiMessageContent(content.GetType().Name, error.Message, content.AdditionalProperties);
            }
            else if (content is UsageContent usageContent)
            {
                aiMsgContent = new AiMessageContent(content.GetType().Name, usageContent.Details, content.AdditionalProperties);
            }
            return aiMsgContent;
        })
            .Where(x => x != null)
            .Select(x => x!)
            .ToList();

        var aiMessage = new AiMessage
            (
                update.MessageId ?? "",
                update.AuthorName,
                roleStr,
                aiMsgContents,
                update.AdditionalProperties
            );

        return aiMessage;
    }

    /// <summary>
    /// Convert AssistantMessage to ClaudeCodeMessage.
    /// </summary>
    private AiMessage ConvertAssistantMessage(AssistantMessage message)
    {

        var c = ConvertContent(message.Content);
        return new AiMessage(
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
    private AiMessage ConvertResultMessage(ResultMessage message)
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

        return new AiMessage
        ("", "result", ChatRole.System.Value,
        [new AiMessageContent("TextContent", message.Result ?? string.Empty)]
        );

    }

    /// <summary>
    /// Convert SystemMessage to ClaudeCodeMessage.
    /// </summary>
    private AiMessage ConvertSystemMessage(SystemMessage message)
    {
        // Convert Data dictionary to readable string
        var dataContent = string.Join("\n",
            message.Data.Select(kvp => $"{kvp.Key}: {kvp.Value}"));

        return new AiMessage
        ("", message.Subtype, ChatRole.System.Value,
        [ new AiMessageContent("TextContent", dataContent) ]
        );
    }

    /// <summary>
    /// Convert UserMessage to ClaudeCodeMessage.
    /// </summary>
    private AiMessage ConvertUserMessage(UserMessage message)
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
        var ccMsg = new AiMessage(
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
