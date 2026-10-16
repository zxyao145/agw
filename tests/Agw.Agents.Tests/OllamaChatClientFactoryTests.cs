using System.Net.Http.Headers;
using Agw.Agents.Execution.Agents.Runtime;
using Agw.Providers.Contracts;
using Agw.Providers.Contracts.References;
using Agw.Shared.Exceptions;
using Agw.Testing;
using Microsoft.Extensions.AI;
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

    private static ModelProviderModelSnapshot CreateModel() => new(Guid.CreateVersion7(), "qwen3:4b", 8192, 2048);

    private static ModelProviderProviderSnapshot CreateProvider(
        string endpoint,
        IReadOnlyList<ProviderAuthConfigSnapshot> authConfigs
    ) => new(Guid.CreateVersion7(), "ollama", ProviderType.OllamaNative, endpoint, authConfigs);
}
