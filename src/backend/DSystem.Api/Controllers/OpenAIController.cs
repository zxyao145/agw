using DSystem.Appliaction;
using DSystem.Shared;
using DSystem.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Text;

namespace DSystem.Api.Controllers;

/// <summary>
/// OpenAI-compatible API endpoint for seamless migration from OpenAI API
/// </summary>
[ApiController]
[Route("v1")]
public class OpenAIController : ControllerBase
{
    private readonly AgentRuntimeService _agentRuntimeService;

    public OpenAIController(AgentRuntimeService agentRuntimeService)
    {
        _agentRuntimeService = agentRuntimeService;
    }

    /// <summary>
    /// OpenAI Chat Completions API endpoint
    /// Standard OpenAI format with messages array
    /// </summary>
    /// <param name="request">Chat completion request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Chat completion response</returns>
    [HttpPost("chat/completions")]
    public async Task<IActionResult> CreateChatCompletionAsync(
        [FromBody] OpenAIChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        // Validate model (Agent ID)
        if (!Guid.TryParse(request.Model, out var agentId))
        {
            return BadRequest(new { error = new { message = "Invalid model ID. Must be a valid GUID (Agent ID).", type = "invalid_request_error" } });
        }

        // Extract user input from messages
        var userMessage = request.Messages.LastOrDefault(m => m.Role == "user");
        if (userMessage?.Content == null)
        {
            return BadRequest(new { error = new { message = "No user message found in request.", type = "invalid_request_error" } });
        }

        var threadId = request.PreviousResponseId ?? string.Empty;

        // Handle streaming vs non-streaming
        if (request.Stream)
        {
            return await StreamChatCompletionAsync(agentId, threadId, userMessage.Content, request.Model, cancellationToken);
        }
        else
        {
            return await CompleteChatAsync(agentId, threadId, userMessage.Content, request.Model, cancellationToken);
        }
    }



    /// <summary>
    /// Non-streaming chat completion
    /// </summary>
    private async Task<IActionResult> CompleteChatAsync(
        Guid agentId,
        string threadId,
        string input,
        string model,
        CancellationToken cancellationToken)
    {
        var result = await _agentRuntimeService.ExecuteAsync(agentId, threadId, input, cancellationToken);
        if (result == null)
        {
            return NotFound(new { error = new { message = "Agent not found or execution failed.", type = "invalid_request_error" } });
        }

        // Merge messages by messageId (same as frontend logic)
        var mergedContent = new StringBuilder();

        // Create a dictionary to track first occurrence index for ordering
        var messageIndexes = new Dictionary<string, int>();
        for (int i = 0; i < result.Messages.Count; i++)
        {
            if (!messageIndexes.ContainsKey(result.Messages[i].MessageId))
            {
                messageIndexes[result.Messages[i].MessageId] = i;
            }
        }

        var messageGroups = result.Messages
            .GroupBy(m => m.MessageId)
            .OrderBy(g => messageIndexes[g.Key]);

        foreach (var group in messageGroups)
        {
            foreach (var msg in group)
            {
                var textContent = msg.Contents.Find(c => c.Type == "text");
                mergedContent.Append(textContent?.Content?.ToString() ?? "");
            }
        }

        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var response = new OpenAIChatCompletionResponse
        {
            Id = completionId,
            Created = created,
            Model = model,
            ThreadId = result.ThreadId,
            Choices = new List<OpenAIChatChoice>
            {
                new OpenAIChatChoice
                {
                    Index = 0,
                    Message = new OpenAIChatMessage
                    {
                        Role = "assistant",
                        Content = mergedContent.ToString()
                    },
                    FinishReason = "stop"
                }
            },
            Usage = new OpenAIUsage
            {
                // Note: Actual token counting would require tokenizer integration
                PromptTokens = 0,
                CompletionTokens = 0,
                TotalTokens = 0
            }
        };

        return Ok(response);
    }

    /// <summary>
    /// Streaming chat completion
    /// </summary>
    private async Task<IActionResult> StreamChatCompletionAsync(
        Guid agentId,
        string threadId,
        string input,
        string model,
        CancellationToken cancellationToken)
    {
        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        var completionId = $"chatcmpl-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var isFirstChunk = true;
        var currentThreadId = threadId;

        try
        {
            await foreach (var message in _agentRuntimeService.ExecuteStreamingAsync(agentId, threadId, input, cancellationToken))
            {
                // First chunk includes role
                if (isFirstChunk)
                {
                    var firstChunk = new OpenAIChatCompletionChunk
                    {
                        Id = completionId,
                        Created = created,
                        Model = model,
                        ThreadId = currentThreadId,
                        Choices = new List<OpenAIChatChunkChoice>
                        {
                            new OpenAIChatChunkChoice
                            {
                                Index = 0,
                                Delta = new OpenAIChatDelta
                                {
                                    Role = "assistant",
                                    Content = ""
                                },
                                FinishReason = null
                            }
                        }
                    };

                    var firstJson = JsonUtil.Serialize(firstChunk);
                    await WriteSSEDataAsync($"data: {firstJson}\n\n", cancellationToken);
                    isFirstChunk = false;
                }

                // Content chunks
                var chunk = new OpenAIChatCompletionChunk
                {
                    Id = threadId,
                    Created = created,
                    Model = model,
                    ThreadId = currentThreadId,
                    Choices = new List<OpenAIChatChunkChoice>
                    {
                        new OpenAIChatChunkChoice
                        {
                            Index = 0,
                            Delta = new OpenAIChatDelta
                            {
                                Content = message.Contents.Find(c => c.Type == "text")?.Content?.ToString() ?? ""
                            },
                            FinishReason = null
                        }
                    }
                };

                var json = JsonUtil.Serialize(chunk);
                await WriteSSEDataAsync($"data: {json}\n\n", cancellationToken);
            }

            // Final chunk with finish_reason
            var finalChunk = new OpenAIChatCompletionChunk
            {
                Id = completionId,
                Created = created,
                Model = model,
                ThreadId = currentThreadId,
                Choices = new List<OpenAIChatChunkChoice>
                {
                    new OpenAIChatChunkChoice
                    {
                        Index = 0,
                        Delta = new OpenAIChatDelta(),
                        FinishReason = "stop"
                    }
                }
            };

            var finalJson = JsonUtil.Serialize(finalChunk);
            await WriteSSEDataAsync($"data: {finalJson}\n\n", cancellationToken);

            // Send [DONE] marker
            await WriteSSEDataAsync("data: [DONE]\n\n", cancellationToken);
        }
        catch (Exception ex)
        {
            // Send error in SSE format
            var errorData = JsonUtil.Serialize(new { error = new { message = ex.Message, type = "server_error" } });
            await WriteSSEDataAsync($"data: {errorData}\n\n", cancellationToken);
        }

        return new EmptyResult();
    }


    /// <summary>
    /// OpenAI Responses API endpoint
    /// Uses input field instead of messages array, supports instructions
    /// </summary>
    /// <param name="request">Responses API request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Responses API response</returns>
    [HttpPost("responses")]
    public async Task<IActionResult> CreateResponseAsync(
        [FromBody] OpenAiMessageInput request,
        CancellationToken cancellationToken)
    {
        // Validate model (Agent ID)
        if (string.IsNullOrWhiteSpace(request.Model) || !Guid.TryParse(request.Model, out var agentId))
        {
            return BadRequest(new { error = new { message = "Invalid model ID. Must be a valid GUID (Agent ID).", type = "invalid_request_error" } });
        }

        // Extract input - can be string or message array
        string inputText;
        if (!string.IsNullOrWhiteSpace(request.Input))
        {
            inputText = request.Input;
        }
        else if (request.Inputs != null && request.Inputs.Count > 0)
        {
            // For multimodal input, extract text content
            var textContent = request.Inputs
                .Where(i => i.Contents != null)
                .SelectMany(i => i.Contents!)
                .Where(c => c.Type == "text" && !string.IsNullOrWhiteSpace(c.Text))
                .Select(c => c.Text)
                .FirstOrDefault();

            inputText = textContent ?? string.Empty;
        }
        else
        {
            return BadRequest(new { error = new { message = "No input found in request.", type = "invalid_request_error" } });
        }

        if (string.IsNullOrWhiteSpace(inputText))
        {
            return BadRequest(new { error = new { message = "Input cannot be empty.", type = "invalid_request_error" } });
        }


        var threadId = string.IsNullOrWhiteSpace(request.PreviousResponseId)
            ? Guid.NewGuid().ToString() : request.PreviousResponseId;

        // Handle streaming vs non-streaming
        if (request.Stream == true)
        {
            return await StreamResponseAsync(agentId, threadId, inputText, request.Model, cancellationToken);
        }
        else
        {
            return await CompleteResponseAsync(agentId, threadId, inputText, request.Model, cancellationToken);
        }
    }

    /// <summary>
    /// Non-streaming response (Responses API format)
    /// </summary>
    private async Task<IActionResult> CompleteResponseAsync(
        Guid agentId,
        string threadId,
        string input,
        string model,
        CancellationToken cancellationToken)
    {
        var result = await _agentRuntimeService.ExecuteAsync(agentId, threadId, input, cancellationToken);
        if (result == null)
        {
            return NotFound(new { error = new { message = "Agent not found or execution failed.", type = "invalid_request_error" } });
        }

        // Merge messages by messageId
        var mergedContent = new StringBuilder();
        var messageIndexes = new Dictionary<string, int>();
        for (int i = 0; i < result.Messages.Count; i++)
        {
            var msgId = result.Messages[i].MessageId!;
            if (!messageIndexes.ContainsKey(msgId))
            {
                messageIndexes[result.Messages[i].MessageId] = i;
            }
        }

        var messageGroups = result.Messages
            .GroupBy(m => m.MessageId)
            .OrderBy(g => messageIndexes[g.Key]);

        foreach (var group in messageGroups)
        {
            foreach (var msg in group)
            {
                var textContent = msg.Contents.Find(c => c.Type == "text");
                mergedContent.Append(textContent?.Content?.ToString() ?? "");
            }
        }

        var responseId = $"resp_{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var response = new ResponsesApiResponse
        {
            Id = responseId,
            CreatedAt = created,
            Model = model,
            PreviousResponseId = result.ThreadId,
            Output = new List<ResponsesOutputItem>
            {
                new ResponsesOutputItem
                {
                    Index = 0,
                    Type = "message",
                    Message = new ResponsesMessage
                    {
                        Role = "assistant",
                        Content = mergedContent.ToString()
                    }
                }
            },
            Usage = new ResponsesUsage
            {
                InputTokens = 0,
                OutputTokens = 0,
                TotalTokens = 0
            }
        };

        return Ok(response);
    }

    /// <summary>
    /// Streaming response (Responses API format)
    /// </summary>
    private async Task<IActionResult> StreamResponseAsync(
        Guid agentId,
        string threadId,
        string input,
        string model,
        CancellationToken cancellationToken)
    {
        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";

        var responseId = $"resp_{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            await foreach (var message in _agentRuntimeService.ExecuteStreamingAsync(agentId, threadId, input, cancellationToken))
            {
                #region first chunk
                //if (isFirstEvent)
                //{
                //    // Send response.created event
                //    var createdEvent = new ResponsesStreamEvent
                //    {
                //        Type = "response.created",
                //        Response = new ResponsesApiResponse
                //        {
                //            Id = responseId,
                //            CreatedAt = created,
                //            Model = model,
                //            Status = "in_progress",
                //            Output = new List<ResponsesOutputItem>()
                //        }
                //    };
                //    var createdJson = JsonUtil.Serialize(createdEvent);
                //    await WriteSSEDataAsync($"event: response.created\ndata: {createdJson}\n\n", cancellationToken);

                //    // Send output_item.added event
                //    var addedEvent = new ResponsesStreamEvent
                //    {
                //        Type = "response.output_item.added",
                //        Index = 0
                //    };
                //    var addedJson = JsonUtil.Serialize(addedEvent);
                //    await WriteSSEDataAsync($"event: response.output_item.added\ndata: {addedJson}\n\n", cancellationToken);

                //    isFirstEvent = false;
                //} 
                #endregion

                var contents = new List<Content>();
                contents.Add(new Content
                {
                    Type = "text",
                    Annotations = new List<string>(),
                    Text = message.Contents.Find(c => c.Type == "text")?.Content?.ToString() ?? ""
                });

                // Send content delta event
                var deltaEvent = new ResponsesStreamEvent
                {
                    Id = threadId,
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Model = model,
                    Output = new List<OutputItem>
                    {
                        new OutputItem()
                        {
                            Id = message.MessageId,
                            Type = "message",
                            Status =  "completed",
                            Role = message.Role,
                            Content = contents
                        }
                    }
                };
                var deltaJson = JsonUtil.Serialize(deltaEvent);
                await WriteSSEDataAsync($"event: response.output_item.content.delta\ndata: {deltaJson}\n\n", cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Send error event
            var errorEvent = new
            {
                type = "error",
                error = new { message = ex.Message, type = "server_error" }
            };
            var errorJson = JsonUtil.Serialize(errorEvent);
            await WriteSSEDataAsync($"event: error\ndata: {errorJson}\n\n", cancellationToken);
        }

        return new EmptyResult();
    }

    /// <summary>
    /// Helper method to write SSE data with proper encoding
    /// </summary>
    private async Task WriteSSEDataAsync(string data, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        await Response.Body.WriteAsync(bytes, cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }
}
