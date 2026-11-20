using System.Net;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Middleware.ModelInput;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests;

public sealed class AnthropicPromptCachingTests
{
    [Theory]
    [InlineData("/gateway/anthropic", "vendor/custom-model", false, false)]
    [InlineData("/gateway/anthropic", "vendor/custom-model", true, true)]
    [InlineData("/proxy/provider/messages", "another-model", false, true)]
    [InlineData("/proxy/provider/messages", "another-model", true, false)]
    public async Task GetResponse_CustomEndpointAndModel_AddsFiveMinuteTopLevelCacheControl(
        string endpointPath,
        string model,
        bool streaming,
        bool useRawRepresentationFactory
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await LocalAnthropicServer.StartAsync(cancellationToken);
        await using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var endpoint = $"{server.Address}{endpointPath}";
        var sdkClient = new AnthropicClient(
            new ClientOptions
            {
                ApiKey = "local-test-key",
                BaseUrl = endpoint,
                HttpClient = services.GetRequiredService<IHttpClientFactory>().CreateClient(),
            }
        );
        var cachingClient = AnthropicPromptCachingChatClient.Create(sdkClient, model);
        using var client = useRawRepresentationFactory
            ? AnthropicReasoningChatClient.Create(cachingClient, new Uri(endpoint))
            : cachingClient;
        var options = new ChatOptions
        {
            MaxOutputTokens = 16384,
            ModelId = "chat-options-model",
            Temperature = 0.2f,
            Tools = [AIFunctionFactory.Create(TimeProvider.System.GetUtcNow, "get_utc_now")],
            Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low },
            RawRepresentationFactory = useRawRepresentationFactory
                ? _ => new MessageCreateParams
                {
                    MaxTokens = 12288,
                    Messages = [],
                    Model = "raw-options-model",
                    Thinking = new ThinkingConfigEnabled { BudgetTokens = 2048 },
                }
                : null,
        };

        var exception = await Record.ExceptionAsync(async () =>
        {
            if (streaming)
            {
                await client
                    .GetStreamingResponseAsync([new(ChatRole.User, "question")], options, cancellationToken)
                    .ToChatResponseAsync(cancellationToken: cancellationToken);
            }
            else
            {
                await client.GetResponseAsync([new(ChatRole.User, "question")], options, cancellationToken);
            }
        });

        var apiException = Assert.IsType<AnthropicBadRequestException>(exception);
        Assert.Equal(HttpStatusCode.BadRequest, apiException.StatusCode);
        Assert.True(server.Request.IsCompletedSuccessfully);
        using var request = await server.Request;
        var body = request.RootElement;
        var cacheControl = body.GetProperty("cache_control");
        Assert.Equal("ephemeral", cacheControl.GetProperty("type").GetString());
        Assert.False(cacheControl.TryGetProperty("ttl", out _));
        Assert.Equal(
            useRawRepresentationFactory ? "raw-options-model" : "chat-options-model",
            body.GetProperty("model").GetString()
        );
        Assert.Equal(useRawRepresentationFactory ? 12288 : 16384, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.2, body.GetProperty("temperature").GetDouble(), 0.00001);
        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(
            useRawRepresentationFactory ? 2048 : 1024,
            body.GetProperty("thinking").GetProperty("budget_tokens").GetInt32()
        );
        Assert.Equal("get_utc_now", body.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    internal sealed class LocalAnthropicServer : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly TaskCompletionSource<JsonDocument> _request;

        public string Address { get; }
        public Task<JsonDocument> Request => _request.Task;

        private LocalAnthropicServer(
            WebApplication application,
            string address,
            TaskCompletionSource<JsonDocument> request
        )
        {
            _application = application;
            Address = address;
            _request = request;
        }

        public static async Task<LocalAnthropicServer> StartAsync(CancellationToken cancellationToken)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();
            var request = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            application.MapPost(
                "/{**path}",
                async (HttpContext context) =>
                {
                    request.TrySetResult(
                        await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken)
                    );
                    return Results.Json(
                        new
                        {
                            type = "error",
                            error = new { type = "invalid_request_error", message = "local request observation" },
                        },
                        statusCode: (int)HttpStatusCode.BadRequest
                    );
                }
            );
            await application.StartAsync(cancellationToken);
            var address = Assert.Single(
                application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            );
            return new LocalAnthropicServer(application, address, request);
        }

        public async ValueTask DisposeAsync() => await _application.DisposeAsync();
    }
}
