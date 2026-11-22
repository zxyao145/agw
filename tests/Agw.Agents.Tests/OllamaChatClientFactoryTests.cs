using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Providers.Contracts;
using Agw.Providers.Contracts.References;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OllamaSharp;

namespace Agw.Agents.Tests;

public class OllamaChatClientFactoryTests
{
    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/", "http://localhost:11434/api/chat")]
    [InlineData(" http://localhost:11434/ ", "http://localhost:11434/", "http://localhost:11434/api/chat")]
    [InlineData("http://ollama.test/ollama", "http://ollama.test/ollama/", "http://ollama.test/ollama/api/chat")]
    [InlineData(
        "https://ollama.test/team/ollama/",
        "https://ollama.test/team/ollama/",
        "https://ollama.test/team/ollama/api/chat"
    )]
    public async Task CreateClient_EndpointWithPathPrefix_PreservesPrefix(
        string endpoint,
        string expectedBaseAddress,
        string expectedChatUri
    )
    {
        using var httpClientFactory = new RecordingHttpClientFactory();
        var factory = new OllamaChatClientFactory(httpClientFactory);

        using var client = factory.CreateClient(CreateModel(), CreateProvider(endpoint, []));

        var metadata = client.GetService<ChatClientMetadata>();
        Assert.NotNull(metadata);
        Assert.Equal("ollama", metadata.ProviderName);
        Assert.Equal("qwen3:4b", metadata.DefaultModelId);
        Assert.Equal(new Uri(expectedBaseAddress), metadata.ProviderUri);
        var httpClient = Assert.Single(httpClientFactory.Clients);
        Assert.Equal(new Uri(expectedBaseAddress), httpClient.BaseAddress);
        // HttpClient 按 BaseAddress 解析 SDK 的相对路径 api/chat。
        // HttpClient resolves the SDK's relative api/chat path against BaseAddress.
        Assert.Equal(new Uri(expectedChatUri), new Uri(httpClient.BaseAddress!, "api/chat"));
        Assert.NotNull(client.GetService<OllamaApiClient>());

        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            httpClient.GetAsync("api/version", TestContext.Current.CancellationToken)
        );
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/ollama")]
    [InlineData("ftp://ollama.test")]
    [InlineData("")]
    public void CreateClient_InvalidEndpoint_ThrowsInvalidUrl(string endpoint)
    {
        using var httpClientFactory = new RecordingHttpClientFactory();
        var factory = new OllamaChatClientFactory(httpClientFactory);

        var exception = Assert.Throws<AgwException>(() =>
            factory.CreateClient(CreateModel(), CreateProvider(endpoint, []))
        );

        Assert.Equal(ErrorCodes.InvalidUrl.Code, exception.Code);
        Assert.Empty(httpClientFactory.Clients);
    }

    [Fact]
    public void CreateClient_EnabledApiKeys_UsesBearerTokenAndIgnoresUnusableConfigs()
    {
        using var httpClientFactory = new RecordingHttpClientFactory();
        var factory = new OllamaChatClientFactory(httpClientFactory);

        using var withoutKey = factory.CreateClient(
            CreateModel(),
            CreateProvider(
                "http://localhost:11434",
                [new ProviderAuthConfigSnapshot(true, "  "), new ProviderAuthConfigSnapshot(false, "disabled-key")]
            )
        );
        using var withKey = factory.CreateClient(
            CreateModel(),
            CreateProvider(
                "http://localhost:11434",
                [
                    new ProviderAuthConfigSnapshot(true, null),
                    new ProviderAuthConfigSnapshot(false, "disabled-key"),
                    new ProviderAuthConfigSnapshot(true, " secret "),
                ]
            )
        );

        Assert.Equal(2, httpClientFactory.Clients.Count);
        Assert.Null(httpClientFactory.Clients[0].DefaultRequestHeaders.Authorization);
        Assert.Equal(
            new AuthenticationHeaderValue("Bearer", "secret"),
            httpClientFactory.Clients[1].DefaultRequestHeaders.Authorization
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateClient_NativeResponse_ReportsCachedInputTokens(bool streaming)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await LocalOllamaServer.StartAsync(cancellationToken);
        await using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var factory = new OllamaChatClientFactory(services.GetRequiredService<IHttpClientFactory>());
        using var client = factory.CreateClient(CreateModel(), CreateProvider($"{server.Address}/ollama", []));

        ChatResponse response;
        if (streaming)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (
                var update in client
                    .GetStreamingResponseAsync([new(ChatRole.User, "7")], cancellationToken: cancellationToken)
                    .WithCancellation(cancellationToken)
            )
            {
                updates.Add(update);
            }

            var streamedUsage = Assert.Single(updates.SelectMany(update => update.Contents).OfType<UsageContent>());
            Assert.Equal(7, streamedUsage.Details.CachedInputTokenCount);
            response = updates.ToChatResponse();
        }
        else
        {
            response = await client.GetResponseAsync([new(ChatRole.User, "7")], cancellationToken: cancellationToken);
        }

        Assert.Equal(streaming ? "partial answer" : "answer", Assert.Single(response.Messages).Text);
        Assert.Equal(12, response.Usage?.InputTokenCount);
        Assert.Equal(7, response.Usage?.CachedInputTokenCount);
        Assert.Equal(2, response.Usage?.OutputTokenCount);
        Assert.Equal(14, response.Usage?.TotalTokenCount);
        var responseProperties = streaming
            ? response.Messages.Single().AdditionalProperties
            : response.AdditionalProperties;
        Assert.Equal(TimeSpan.FromMilliseconds(0.0001), responseProperties!["total_duration"]);

        var request = Assert.Single(server.Requests);
        Assert.Equal("/ollama/api/chat", request.Path);
        Assert.Equal("qwen3:4b", request.Body.RootElement.GetProperty("model").GetString());
        Assert.Equal(streaming, request.Body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("7", request.Body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CreateClient_ResponseWithoutCachedInputTokens_LeavesCachedUsageUnset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await LocalOllamaServer.StartAsync(cancellationToken, includeCachedInputTokens: false);
        await using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var factory = new OllamaChatClientFactory(services.GetRequiredService<IHttpClientFactory>());
        using var client = factory.CreateClient(CreateModel(), CreateProvider(server.Address, []));

        var response = await client.GetResponseAsync([new(ChatRole.User, "7")], cancellationToken: cancellationToken);

        Assert.Null(response.Usage?.CachedInputTokenCount);
    }

    [Fact]
    public async Task CreateClient_ResponseWithZeroCachedInputTokens_ReportsZero()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await LocalOllamaServer.StartAsync(cancellationToken);
        await using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var factory = new OllamaChatClientFactory(services.GetRequiredService<IHttpClientFactory>());
        using var client = factory.CreateClient(CreateModel(), CreateProvider(server.Address, []));

        var response = await client.GetResponseAsync([new(ChatRole.User, "0")], cancellationToken: cancellationToken);

        Assert.Equal(0, response.Usage?.CachedInputTokenCount);
    }

    [Fact]
    public async Task CreateClient_ConcurrentResponses_AssociatesCachedInputTokensPerCall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await LocalOllamaServer.StartAsync(cancellationToken);
        await using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var factory = new OllamaChatClientFactory(services.GetRequiredService<IHttpClientFactory>());
        using var client = factory.CreateClient(CreateModel(), CreateProvider(server.Address, []));

        var responses = await Task.WhenAll(
            client.GetResponseAsync([new(ChatRole.User, "3")], cancellationToken: cancellationToken),
            client.GetResponseAsync([new(ChatRole.User, "9")], cancellationToken: cancellationToken)
        );

        Assert.Equal([3, 9], responses.Select(response => response.Usage?.CachedInputTokenCount));
    }

    [Fact]
    public async Task CreateClient_CanceledRequest_DoesNotCarryCachedInputTokensIntoNextCall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await LocalOllamaServer.StartAsync(cancellationToken);
        await using var services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
        var factory = new OllamaChatClientFactory(services.GetRequiredService<IHttpClientFactory>());
        using var client = factory.CreateClient(CreateModel(), CreateProvider(server.Address, []));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetResponseAsync([new(ChatRole.User, "3")], cancellationToken: cancellation.Token)
        );
        var response = await client.GetResponseAsync([new(ChatRole.User, "9")], cancellationToken: cancellationToken);

        Assert.Equal(9, response.Usage?.CachedInputTokenCount);
        Assert.Single(server.Requests);
    }

    internal sealed class LocalOllamaServer : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly WebApplication _application;
        private readonly ConcurrentQueue<RecordedRequest> _requests;

        public string Address { get; }
        public IReadOnlyCollection<RecordedRequest> Requests => _requests.ToArray();

        private LocalOllamaServer(WebApplication application, string address, ConcurrentQueue<RecordedRequest> requests)
        {
            _application = application;
            Address = address;
            _requests = requests;
        }

        public static async Task<LocalOllamaServer> StartAsync(
            CancellationToken cancellationToken,
            bool includeCachedInputTokens = true
        )
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();
            var requests = new ConcurrentQueue<RecordedRequest>();
            application.MapPost(
                "/{**path}",
                async (HttpContext context) =>
                {
                    var request = await JsonDocument.ParseAsync(
                        context.Request.Body,
                        cancellationToken: context.RequestAborted
                    );
                    requests.Enqueue(new RecordedRequest(context.Request.Path.Value!, request));
                    var userMessage = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
                    var cachedInputTokens = int.Parse(userMessage!, CultureInfo.InvariantCulture);
                    var streaming = request.RootElement.GetProperty("stream").GetBoolean();
                    context.Response.ContentType = streaming ? "application/x-ndjson" : "application/json";

                    if (streaming)
                    {
                        await WriteResponseAsync(
                            context,
                            new
                            {
                                model = "qwen3:4b",
                                created_at = "2026-01-01T00:00:00Z",
                                message = new { role = "assistant", content = "partial" },
                                done = false,
                            }
                        );
                    }

                    var finalResponse = new Dictionary<string, object?>
                    {
                        ["model"] = "qwen3:4b",
                        ["created_at"] = "2026-01-01T00:00:00Z",
                        ["message"] = new { role = "assistant", content = streaming ? " answer" : "answer" },
                        ["done"] = true,
                        ["done_reason"] = "stop",
                        ["total_duration"] = 100,
                        ["load_duration"] = 20,
                        ["prompt_eval_count"] = 12,
                        ["prompt_eval_duration"] = 60,
                        ["eval_count"] = 2,
                        ["eval_duration"] = 20,
                    };
                    if (includeCachedInputTokens)
                    {
                        finalResponse["prompt_eval_cached_count"] = cachedInputTokens;
                    }

                    await WriteResponseAsync(context, finalResponse);
                }
            );
            await application.StartAsync(cancellationToken);
            var address = Assert.Single(
                application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            );
            return new LocalOllamaServer(application, address, requests);
        }

        public async ValueTask DisposeAsync()
        {
            await _application.DisposeAsync();
            foreach (var request in _requests)
            {
                request.Body.Dispose();
            }
        }

        private static async Task WriteResponseAsync(HttpContext context, object response)
        {
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(response, JsonOptions) + "\n",
                context.RequestAborted
            );
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    internal sealed record RecordedRequest(string Path, JsonDocument Body);

    private static ModelProviderModelSnapshot CreateModel() => new(Guid.CreateVersion7(), "qwen3:4b", 8192, 2048);

    private static ModelProviderProviderSnapshot CreateProvider(
        string endpoint,
        IReadOnlyList<ProviderAuthConfigSnapshot> authConfigs
    ) => new(Guid.CreateVersion7(), "ollama", ProviderType.OllamaNative, endpoint, authConfigs);
}
