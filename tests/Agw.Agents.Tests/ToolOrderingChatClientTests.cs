using Agw.Agents.Execution.Agents.Middleware.ModelInput;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Tests;

public sealed class ToolOrderingChatClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetResponseAsync_ToolsFromAllSources_MatchesChatCompletionsOrder(bool streaming)
    {
        var model = new RecordingModelClient();
        using var client = new ToolOrderingChatClient(model);
        var chatCompletionsModel = new RecordingModelClient();
        using var chatCompletions = OpenAiChatCompletionsPromptCachingChatClient.Create(
            chatCompletionsModel,
            new Uri("https://provider.example.test/v1"),
            "test-model",
            stableInstructions: null
        );
        var options = new ChatOptions
        {
            Tools = [Function("b_tool"), new HostedWebSearchTool(), Function("a_tool"), Function("A_tool")],
        };
        ChatMessage[] messages = [new(ChatRole.User, "hi")];
        await SendAsync(client, messages, options, streaming);
        await SendAsync(chatCompletions, messages, options, streaming);

        var actual = Assert.Single(model.Requests).ToolNames;
        Assert.Equal(["A_tool", "a_tool", "b_tool", "web_search"], actual);
        Assert.Equal(Assert.Single(chatCompletionsModel.Requests).ToolNames, actual);
        Assert.Equal(["b_tool", "web_search", "a_tool", "A_tool"], options.Tools.Select(tool => tool.Name));
    }

    private static async Task SendAsync(IChatClient client, ChatMessage[] messages, ChatOptions options, bool streaming)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (streaming)
            await foreach (var _ in client.GetStreamingResponseAsync(messages, options, cancellationToken)) { }
        else
            await client.GetResponseAsync(messages, options, cancellationToken);
    }

    private static AIFunction Function(string name) =>
        AIFunctionFactory.Create((Func<string>)(() => name), new AIFunctionFactoryOptions { Name = name });
}
