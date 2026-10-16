using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Agw.Providers.Contracts;
using Agw.Providers.Contracts.Manager;
using Agw.Providers.Infrastructure;
using Agw.Shared.Exceptions;
using Agw.Testing;

namespace Agw.Projects.Tests;

public class ProviderModelDiscoveryServiceTests
{
    [Theory]
    [InlineData(ProviderType.OpenAIChatCompletions)]
    [InlineData(ProviderType.OpenAIResponses)]
    public async Task DiscoverAsync_SupportedProvider_RequestsModelsWithBearerToken(ProviderType providerType)
    {
        HttpRequestMessage? capturedRequest = null;
        var service = CreateService(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\" gpt-4o \"},{\"id\":\"gpt-4o\"},{\"id\":\"\"},{\"id\":\"GPT-4O\"}]}",
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        });

        var result = await service.DiscoverAsync(
            new ProviderModelDiscoveryRequest(providerType, "https://example.test/v1/", "secret"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["gpt-4o", "GPT-4O"], result.ModelNames);
        Assert.NotNull(capturedRequest);
        Assert.Equal(new Uri("https://example.test/v1/models"), capturedRequest.RequestUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "secret"), capturedRequest.Headers.Authorization);
    }

    [Fact]
    public async Task DiscoverAsync_UnsupportedProvider_ThrowsBeforeSendingRequest()
    {
        var requestSent = false;
        var service = CreateService(_ =>
        {
            requestSent = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.DiscoverAsync(
                new ProviderModelDiscoveryRequest(ProviderType.Anthropic, "https://example.test/v1", "secret"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.UnsupportedProviderType.Code, exception.Code);
        Assert.False(requestSent);
    }

    [Fact]
    public async Task DiscoverAsync_EmptyApiKey_ThrowsBeforeSendingRequest()
    {
        var requestSent = false;
        var service = CreateService(_ =>
        {
            requestSent = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.DiscoverAsync(
                new ProviderModelDiscoveryRequest(ProviderType.OpenAIChatCompletions, "https://example.test/v1", "  "),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ProviderModelDiscoveryApiKeyRequired.Code, exception.Code);
        Assert.False(requestSent);
    }

    [Fact]
    public async Task DiscoverAsync_InvalidEndpoint_ThrowsBeforeSendingRequest()
    {
        var requestSent = false;
        var service = CreateService(_ =>
        {
            requestSent = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.DiscoverAsync(
                new ProviderModelDiscoveryRequest(ProviderType.OpenAIChatCompletions, "not-a-url", "secret"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidUrl.Code, exception.Code);
        Assert.False(requestSent);
    }

    [Fact]
    public async Task DiscoverAsync_RemoteFailure_ThrowsDiscoveryFailed()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.DiscoverAsync(
                new ProviderModelDiscoveryRequest(
                    ProviderType.OpenAIChatCompletions,
                    "https://example.test/v1",
                    "secret"
                ),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ProviderModelDiscoveryFailed.Code, exception.Code);
    }

    [Fact]
    public async Task DiscoverAsync_InvalidResponse_ThrowsDiscoveryFailed()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"object\":\"list\"}", Encoding.UTF8, "application/json"),
        });

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.DiscoverAsync(
                new ProviderModelDiscoveryRequest(
                    ProviderType.OpenAIChatCompletions,
                    "https://example.test/v1",
                    "secret"
                ),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ProviderModelDiscoveryFailed.Code, exception.Code);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"data\":{}}")]
    public async Task DiscoverAsync_MalformedResponseShape_ThrowsDiscoveryFailed(string responseBody)
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.DiscoverAsync(
                new ProviderModelDiscoveryRequest(
                    ProviderType.OpenAIChatCompletions,
                    "https://example.test/v1",
                    "secret"
                ),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ProviderModelDiscoveryFailed.Code, exception.Code);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("/ollama")]
    [InlineData("ftp://ollama.test")]
    public async Task DiscoverAsync_OllamaInvalidEndpoint_ThrowsBeforeCreatingClient(string endpoint)
    {
        using var httpClientFactory = new RecordingHttpClientFactory();
        var service = new ProviderModelDiscoveryService(httpClientFactory);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.DiscoverAsync(
                new ProviderModelDiscoveryRequest(ProviderType.OllamaNative, endpoint),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.InvalidUrl.Code, exception.Code);
        Assert.Empty(httpClientFactory.Clients);
    }

    [Theory]
    [InlineData("", null, null, "/api/tags")]
    [InlineData("/ollama", "  ", null, "/ollama/api/tags")]
    [InlineData("/team/ollama/", " secret ", "secret", "/team/ollama/api/tags")]
    public async Task DiscoverAsync_OllamaUnreachableEndpoint_ThrowsDiscoveryFailed(
        string pathPrefix,
        string? apiKey,
        string? expectedToken,
        string expectedTagsPath
    )
    {
        // 占用后释放一个本机端口，连接该端口会被拒绝。
        // Reserve and release a local port so connections to it are refused.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var httpClientFactory = new RecordingHttpClientFactory();
        var service = new ProviderModelDiscoveryService(httpClientFactory);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            service.DiscoverAsync(
                new ProviderModelDiscoveryRequest(
                    ProviderType.OllamaNative,
                    $"http://127.0.0.1:{port}{pathPrefix}",
                    apiKey
                ),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ProviderModelDiscoveryFailed.Code, exception.Code);
        var httpClient = Assert.Single(httpClientFactory.Clients);
        Assert.Equal(expectedTagsPath, new Uri(httpClient.BaseAddress!, "api/tags").AbsolutePath);
        Assert.Equal(
            expectedToken == null ? null : new AuthenticationHeaderValue("Bearer", expectedToken),
            httpClient.DefaultRequestHeaders.Authorization
        );
    }

    private static ProviderModelDiscoveryService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new ProviderModelDiscoveryService(new StubHttpClientFactory(new StubHttpMessageHandler(handler)));
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(_handler(request));
        }
    }
}
