using System.Text.Json;
using Agw.Agents.Execution.Agents.Middleware.ModelInput;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable OPENAI001
#pragma warning disable SCME0001

namespace Agw.Agents.Tests;

public sealed class OpenAiPromptOptimizationTests
{
    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\n")]
    [InlineData(false, "\r\n")]
    [InlineData(true, "\r\n")]
    public async Task Responses_Gpt62StableInstructions_PromotesOnlyStablePrefix(bool streaming, string separator)
    {
        const string endpoint = "https://api.openai.com/v1";
        const string model = "gpt-6.2-sol";
        const string stableInstructions = "stable agent instructions";
        const string requestInstructions = "request-specific instructions";
        await using var fixture = new OpenAiResponsesReasoningChatClientTests.ClientFixture(endpoint);
        using var client = OpenAiResponsesPromptCachingChatClient.Create(
            fixture.Client,
            new Uri(endpoint),
            model,
            stableInstructions
        );
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "question"),
            new(ChatRole.Assistant, [new TextReasoningContent("prior reasoning"), new TextContent("prior answer")]),
        };
        var originalHistory = JsonSerializer.Serialize(history);
        var options = new ChatOptions
        {
            ModelId = model,
            Instructions = $"{stableInstructions}{separator}{requestInstructions}",
            Tools = [AIFunctionFactory.Create((string value) => value, "lookup", "Looks up a value.")],
            ToolMode = ChatToolMode.RequireAny,
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Medium },
            MaxOutputTokens = 128,
        };

        if (streaming)
        {
            await client
                .GetStreamingResponseAsync(history, options, TestContext.Current.CancellationToken)
                .ToChatResponseAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        else
        {
            await client.GetResponseAsync(history, options, TestContext.Current.CancellationToken);
        }

        var payload = Assert.Single(fixture.Handler.Requests);
        var inputs = payload.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal("developer", inputs[0].GetProperty("role").GetString());
        Assert.Equal(stableInstructions, ReadInputText(inputs[0]));
        Assert.Equal(
            1,
            inputs.Count(input =>
                input.TryGetProperty("role", out var role)
                && role.GetString() == "developer"
                && ReadInputText(input) == stableInstructions
            )
        );
        Assert.Equal("user", inputs[1].GetProperty("role").GetString());
        Assert.Equal("question", inputs[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Contains(inputs, input => input.GetProperty("type").GetString() == "reasoning");
        Assert.Equal(requestInstructions, payload.GetProperty("instructions").GetString());
        Assert.Equal("required", payload.GetProperty("tool_choice").GetString());
        Assert.Equal("medium", payload.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("lookup", payload.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal(originalHistory, JsonSerializer.Serialize(history));
    }

    [Fact]
    public async Task Responses_ContinuationWithOpaqueRawOptions_KeepsInstructionsOutOfInput()
    {
        const string endpoint = "https://api.openai.com/v1";
        const string model = "gpt-6.2-sol";
        const string stableInstructions = "stable agent instructions";
        await using var fixture = new OpenAiResponsesReasoningChatClientTests.ClientFixture(endpoint);
        using var client = OpenAiResponsesPromptCachingChatClient.Create(
            fixture.Client,
            new Uri(endpoint),
            model,
            stableInstructions
        );
        var firstOptions = new ChatOptions { ModelId = model, Instructions = stableInstructions };
        await client.GetResponseAsync(
            [new(ChatRole.User, "first turn")],
            firstOptions,
            TestContext.Current.CancellationToken
        );

        var rawFactoryCalls = 0;
        var continuationOptions = new ChatOptions
        {
            ModelId = model,
            Instructions = $"{stableInstructions}\nrequest-specific instructions",
            RawRepresentationFactory = _ =>
            {
                rawFactoryCalls++;
                var rawOptions = new CreateResponseOptions();
                rawOptions.Patch.Set("$.previous_response_id"u8, "resp_previous");
                return rawOptions;
            },
        };
        await client.GetResponseAsync(
            [new(ChatRole.User, "second turn")],
            continuationOptions,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, rawFactoryCalls);
        var firstPayload = fixture.Handler.Requests[0];
        Assert.Equal("developer", firstPayload.GetProperty("input")[0].GetProperty("role").GetString());
        Assert.False(firstPayload.TryGetProperty("instructions", out _));
        var secondPayload = fixture.Handler.Requests[1];
        Assert.Equal("resp_previous", secondPayload.GetProperty("previous_response_id").GetString());
        Assert.Equal(
            $"{stableInstructions}\nrequest-specific instructions",
            secondPayload.GetProperty("instructions").GetString()
        );
        Assert.DoesNotContain(
            secondPayload.GetProperty("input").EnumerateArray(),
            input => input.TryGetProperty("role", out var role) && role.GetString() == "developer"
        );
    }

    [Theory]
    [InlineData("caller override", "caller override")]
    [InlineData(null, null)]
    public async Task Responses_InstructionOverrideOrMissing_PreservesCallerInput(
        string? instructions,
        string? expectedInstructions
    )
    {
        const string endpoint = "https://api.openai.com/v1";
        const string model = "gpt-6.2-sol";
        await using var fixture = new OpenAiResponsesReasoningChatClientTests.ClientFixture(endpoint);
        using var client = OpenAiResponsesPromptCachingChatClient.Create(
            fixture.Client,
            new Uri(endpoint),
            model,
            "stable agent instructions"
        );

        var pending = new Queue<ChatMessage>([new(ChatRole.User, "question")]);
        IEnumerable<ChatMessage> ReadInput()
        {
            while (pending.TryDequeue(out var message))
                yield return message;
        }

        await client.GetResponseAsync(
            ReadInput(),
            new ChatOptions { ModelId = model, Instructions = instructions },
            TestContext.Current.CancellationToken
        );

        var payload = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(
            expectedInstructions is not null,
            payload.TryGetProperty("instructions", out var actualInstructions)
        );
        if (expectedInstructions is not null)
        {
            Assert.Equal(expectedInstructions, actualInstructions.GetString());
        }

        var input = Assert.Single(payload.GetProperty("input").EnumerateArray());
        Assert.Equal("user", input.GetProperty("role").GetString());
        Assert.Equal("question", ReadInputText(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("system")]
    [InlineData("developer")]
    public async Task Responses_PerCallModelOverrideToSupportedModel_UsesPromotion(string? existingRole)
    {
        const string endpoint = "https://api.openai.com/v1";
        const string instructions = "stable agent instructions";
        await using var fixture = new OpenAiResponsesReasoningChatClientTests.ClientFixture(endpoint);
        using var client = OpenAiResponsesPromptCachingChatClient.Create(
            fixture.Client,
            new Uri(endpoint),
            "gpt-6.1-terra",
            instructions
        );
        var history = new List<ChatMessage>();
        if (existingRole is not null)
        {
            history.Add(new(new ChatRole(existingRole), instructions));
        }
        history.Add(new(ChatRole.User, "question"));

        await client.GetResponseAsync(
            history,
            new ChatOptions { ModelId = "gpt-6.2-sol", Instructions = instructions },
            TestContext.Current.CancellationToken
        );

        var payload = Assert.Single(fixture.Handler.Requests);
        Assert.Equal("gpt-6.2-sol", payload.GetProperty("model").GetString());
        var input = payload.GetProperty("input");
        Assert.Equal(2, input.GetArrayLength());
        Assert.Equal("developer", input[0].GetProperty("role").GetString());
        Assert.Equal(instructions, ReadInputText(input[0]));
        Assert.Equal(existingRole ?? "user", history[0].Role.Value);
        Assert.False(payload.TryGetProperty("instructions", out _));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "gpt-6.2-sol", true)]
    [InlineData("https://api.openai.com/v1", "gpt-6.1-terra", false)]
    [InlineData("https://gateway.example.test/v1", "gpt-6.2-sol", false)]
    public async Task Responses_SummaryLeadingSystemMessage_PromotesOnlyForSupportedModels(
        string endpoint,
        string model,
        bool expectedPromotion
    )
    {
        await using var fixture = new OpenAiResponsesReasoningChatClientTests.ClientFixture(endpoint);
        using var client = OpenAiResponsesPromptCachingChatClient.CreateForSummary(
            fixture.Client,
            new Uri(endpoint),
            model
        );
        var summaryInstructions = new ChatMessage(ChatRole.System, "summary instructions")
        {
            AuthorName = "summary",
            MessageId = "summary-prompt",
            AdditionalProperties = new AdditionalPropertiesDictionary { ["source"] = "summary" },
        };
        var messages = new List<ChatMessage> { summaryInstructions, new(ChatRole.User, "source transcript") };
        var originalPrompt = JsonSerializer.Serialize(messages);

        var response = await client.GetResponseAsync(
            messages,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var payload = Assert.Single(fixture.Handler.Requests);
        var input = payload.GetProperty("input");
        Assert.Equal(expectedPromotion ? "developer" : "system", input[0].GetProperty("role").GetString());
        Assert.Equal("summary instructions", ReadInputText(input[0]));
        Assert.Equal("source transcript", ReadInputText(input[1]));
        Assert.Equal(originalPrompt, JsonSerializer.Serialize(messages));
        Assert.Contains(
            response.Messages.SelectMany(message => message.Contents),
            content => content is FunctionCallContent
        );
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "gpt-6.1-terra")]
    [InlineData("https://gateway.example.test/v1", "gpt-6.2-sol")]
    public async Task Responses_UnsupportedEndpointOrModel_PreservesInstructions(string endpoint, string model)
    {
        await using var fixture = new OpenAiResponsesReasoningChatClientTests.ClientFixture(endpoint);
        using var client = OpenAiResponsesPromptCachingChatClient.Create(
            fixture.Client,
            new Uri(endpoint),
            model,
            "stable agent instructions"
        );
        var options = new ChatOptions
        {
            ModelId = model,
            Instructions = "stable agent instructions\nrequest-specific instructions",
        };

        await client.GetResponseAsync([new(ChatRole.User, "question")], options, TestContext.Current.CancellationToken);

        var payload = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(
            "stable agent instructions\nrequest-specific instructions",
            payload.GetProperty("instructions").GetString()
        );
        Assert.DoesNotContain(
            payload.GetProperty("input").EnumerateArray(),
            input => input.TryGetProperty("role", out var role) && role.GetString() == "developer"
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatCompletions_OfficialOlderModel_OrdersToolsAndKeepsStableKey(bool streaming)
    {
        await using var fixture = new OpenAiReasoningChatClientTests.ClientFixture(
            "https://api.openai.com/v1",
            "stable agent instructions"
        );
        var tools = new List<AITool>
        {
            CreateTool("zeta", "Zeta description."),
            CreateTool("alpha", "Alpha description."),
        };
        var options = new ChatOptions
        {
            ModelId = "gpt-5.5",
            Instructions = "stable agent instructions\nrequest-specific first",
            Tools = tools,
            ToolMode = ChatToolMode.RequireAny,
        };

        if (streaming)
        {
            await fixture
                .Client.GetStreamingResponseAsync(
                    [new(ChatRole.User, "first request")],
                    options,
                    TestContext.Current.CancellationToken
                )
                .ToChatResponseAsync(cancellationToken: TestContext.Current.CancellationToken);
        }
        else
        {
            await fixture.Client.GetResponseAsync(
                [new(ChatRole.User, "first request")],
                options,
                TestContext.Current.CancellationToken
            );
        }

        await fixture.Client.GetResponseAsync(
            [new(ChatRole.User, "second request")],
            new ChatOptions
            {
                ModelId = "gpt-5.5",
                Instructions = "stable agent instructions\nrequest-specific second",
                Tools = [CreateTool("alpha", "Alpha description."), CreateTool("zeta", "Zeta description.")],
                ToolMode = ChatToolMode.RequireAny,
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, fixture.Handler.Requests.Count);
        foreach (var payload in fixture.Handler.Requests)
        {
            var toolsPayload = payload.GetProperty("tools").EnumerateArray().ToList();
            Assert.Equal(
                new[] { "alpha", "zeta" },
                toolsPayload.Select(tool => tool.GetProperty("function").GetProperty("name").GetString())
            );
            Assert.Equal(
                "Alpha description.",
                toolsPayload[0].GetProperty("function").GetProperty("description").GetString()
            );
            Assert.Equal(
                "Zeta description.",
                toolsPayload[1].GetProperty("function").GetProperty("description").GetString()
            );
            Assert.True(toolsPayload[0].GetProperty("function").TryGetProperty("parameters", out _));
            Assert.Equal("required", payload.GetProperty("tool_choice").GetString());
            Assert.Equal("gpt-5.5", payload.GetProperty("model").GetString());
        }

        Assert.Equal(
            fixture.Handler.Requests[0].GetProperty("prompt_cache_key").GetString(),
            fixture.Handler.Requests[1].GetProperty("prompt_cache_key").GetString()
        );
        Assert.False(string.IsNullOrEmpty(fixture.Handler.Requests[0].GetProperty("prompt_cache_key").GetString()));
        Assert.Equal(new[] { "zeta", "alpha" }, tools.Select(tool => tool.Name));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "gpt-3.5-turbo", false)]
    [InlineData("https://api.openai.com/v1", "gpt-4", false)]
    [InlineData("https://api.openai.com/v1", "custom-deployment", false)]
    [InlineData("https://api.openai.com/v1", "gpt-5.6", false)]
    [InlineData("https://gateway.example.test/v1", "gpt-5.5", false)]
    [InlineData("https://api.openai.com/v1", "gpt-4.1", true)]
    [InlineData("https://api.openai.com/v1", "gpt-4o", true)]
    [InlineData("https://api.openai.com/v1", "gpt-5.5", true)]
    public async Task ChatCompletions_PromptCacheKey_UsesVerifiedEndpointAndModelFamilies(
        string endpoint,
        string model,
        bool expected
    )
    {
        await using var fixture = new OpenAiReasoningChatClientTests.ClientFixture(
            endpoint,
            "stable agent instructions"
        );
        await fixture.Client.GetResponseAsync(
            [new(ChatRole.User, "question")],
            new ChatOptions { ModelId = model, Instructions = "stable agent instructions\nrequest-specific" },
            TestContext.Current.CancellationToken
        );

        var payload = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(expected, payload.TryGetProperty("prompt_cache_key", out _));
    }

    [Fact]
    public async Task ChatCompletions_SummaryWithoutAgentId_UsesActualStablePrefix()
    {
        await using var fixture = new OpenAiReasoningChatClientTests.ClientFixture("https://api.openai.com/v1");

        await fixture.Client.GetResponseAsync(
            [new(ChatRole.System, "stable summary instructions"), new(ChatRole.User, "source transcript")],
            new ChatOptions { ModelId = "gpt-5.5" },
            TestContext.Current.CancellationToken
        );

        Assert.False(
            string.IsNullOrEmpty(Assert.Single(fixture.Handler.Requests).GetProperty("prompt_cache_key").GetString())
        );
    }

    [Fact]
    public async Task ChatCompletions_WithoutStablePrefix_SkipsAutomaticKey()
    {
        await using var fixture = new OpenAiReasoningChatClientTests.ClientFixture("https://api.openai.com/v1");

        await fixture.Client.GetResponseAsync(
            [new(ChatRole.User, "question")],
            new ChatOptions { ModelId = "gpt-5.5" },
            TestContext.Current.CancellationToken
        );

        Assert.False(Assert.Single(fixture.Handler.Requests).TryGetProperty("prompt_cache_key", out _));
    }

    [Fact]
    public async Task ChatCompletions_PerCallOnlyInstructions_SkipsCacheKey()
    {
        await using var fixture = new OpenAiReasoningChatClientTests.ClientFixture("https://api.openai.com/v1");

        await fixture.Client.GetResponseAsync(
            [new(ChatRole.User, "question")],
            new ChatOptions { ModelId = "gpt-5.5", Instructions = "request-specific instructions" },
            TestContext.Current.CancellationToken
        );

        Assert.False(Assert.Single(fixture.Handler.Requests).TryGetProperty("prompt_cache_key", out _));
    }

    [Fact]
    public async Task ChatCompletions_ThirdPartyGateway_OrdersToolsWithoutCacheKey()
    {
        await using var fixture = new OpenAiReasoningChatClientTests.ClientFixture("https://gateway.example.test/v1");
        var options = new ChatOptions
        {
            ModelId = "gpt-5.5",
            Tools = [CreateTool("zeta", "Zeta description."), CreateTool("alpha", "Alpha description.")],
            ToolMode = ChatToolMode.RequireAny,
        };

        await fixture.Client.GetResponseAsync(
            [new(ChatRole.User, "question")],
            options,
            TestContext.Current.CancellationToken
        );

        var payload = Assert.Single(fixture.Handler.Requests);
        Assert.False(payload.TryGetProperty("prompt_cache_key", out _));
        Assert.Equal(
            new[] { "alpha", "zeta" },
            payload
                .GetProperty("tools")
                .EnumerateArray()
                .Select(tool => tool.GetProperty("function").GetProperty("name").GetString())
        );
        Assert.Equal("required", payload.GetProperty("tool_choice").GetString());
    }

    [Fact]
    public async Task ChatCompletions_ExplicitCallerPromptCacheKey_IsPreserved()
    {
        await using var fixture = new OpenAiReasoningChatClientTests.ClientFixture("https://api.openai.com/v1");
        var rawFactoryCalls = 0;
        var options = new ChatOptions
        {
            ModelId = "gpt-5.5",
            Instructions = "stable agent instructions",
            AdditionalProperties = new() { ["prompt_cache_key"] = "options-key" },
            RawRepresentationFactory = _ =>
            {
                rawFactoryCalls++;
                var rawOptions = new ChatCompletionOptions();
                rawOptions.Patch.Set("$.prompt_cache_key"u8, "caller-key");
                return rawOptions;
            },
        };

        await fixture.Client.GetResponseAsync(
            [new(ChatRole.User, "question")],
            options,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, rawFactoryCalls);
        Assert.Equal("caller-key", Assert.Single(fixture.Handler.Requests).GetProperty("prompt_cache_key").GetString());

        options.RawRepresentationFactory = null;
        await fixture.Client.GetResponseAsync(
            [new(ChatRole.User, "second question")],
            options,
            TestContext.Current.CancellationToken
        );
        Assert.Equal("options-key", fixture.Handler.Requests[1].GetProperty("prompt_cache_key").GetString());
    }

    [Fact]
    public async Task ChatCompletions_CallerRawOptionsWithoutKey_SkipsGeneratedHint()
    {
        await using var fixture = new OpenAiReasoningChatClientTests.ClientFixture(
            "https://api.openai.com/v1",
            "stable agent instructions"
        );
        var rawFactoryCalls = 0;
        var options = new ChatOptions
        {
            ModelId = "gpt-5.5",
            Instructions = "stable agent instructions",
            RawRepresentationFactory = _ =>
            {
                rawFactoryCalls++;
                var rawOptions = new ChatCompletionOptions { Temperature = 0.25f };
                return rawOptions;
            },
        };
        var rawFactory = options.RawRepresentationFactory;

        await fixture.Client.GetResponseAsync(
            [new(ChatRole.User, "question")],
            options,
            TestContext.Current.CancellationToken
        );

        var payload = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(1, rawFactoryCalls);
        Assert.Same(rawFactory, options.RawRepresentationFactory);
        Assert.False(payload.TryGetProperty("prompt_cache_key", out _));
        Assert.Equal(0.25, payload.GetProperty("temperature").GetDouble());
    }

    private static AIFunction CreateTool(string name, string description) =>
        AIFunctionFactory.Create((string value) => value, name, description);

    private static string? ReadInputText(JsonElement input)
    {
        var content = input.GetProperty("content");
        return content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : string.Concat(content.EnumerateArray().Select(part => part.GetProperty("text").GetString()));
    }
}
