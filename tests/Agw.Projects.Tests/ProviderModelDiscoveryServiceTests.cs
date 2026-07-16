using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Agw.Providers.Application;
using Agw.Providers.Contracts.Manager;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Exceptions;

namespace Agw.Projects.Tests;

public class ProviderModelDiscoveryServiceTests
{
    [Theory]
    [InlineData(ProviderType.OpenAIChatCompletions)]
    [InlineData(ProviderType.OpenAIResponses)]
    public async Task DiscoverAsync_SupportedProvider_RequestsModelsWithBearerToken(
        ProviderType providerType)
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
                    "application/json")
            };
        });

        var result = await service.DiscoverAsync(
            new ProviderModelDiscoveryRequest(providerType, "https://example.test/v1/", "secret"),
            TestContext.Current.CancellationToken);

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

        var exception = await Assert.ThrowsAsync<AgwException>(() => service.DiscoverAsync(
            new ProviderModelDiscoveryRequest(ProviderType.Anthropic, "https://example.test/v1", "secret"),
            TestContext.Current.CancellationToken));

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

        var exception = await Assert.ThrowsAsync<AgwException>(() => service.DiscoverAsync(
            new ProviderModelDiscoveryRequest(
                ProviderType.OpenAIChatCompletions,
                "https://example.test/v1",
                "  "),
            TestContext.Current.CancellationToken));

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

        var exception = await Assert.ThrowsAsync<AgwException>(() => service.DiscoverAsync(
            new ProviderModelDiscoveryRequest(
                ProviderType.OpenAIChatCompletions,
                "not-a-url",
                "secret"),
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.InvalidUrl.Code, exception.Code);
        Assert.False(requestSent);
    }

    [Fact]
    public async Task DiscoverAsync_RemoteFailure_ThrowsDiscoveryFailed()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAsync<AgwException>(() => service.DiscoverAsync(
            new ProviderModelDiscoveryRequest(
                ProviderType.OpenAIChatCompletions,
                "https://example.test/v1",
                "secret"),
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ProviderModelDiscoveryFailed.Code, exception.Code);
    }

    [Fact]
    public async Task DiscoverAsync_InvalidResponse_ThrowsDiscoveryFailed()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"object\":\"list\"}", Encoding.UTF8, "application/json")
        });

        var exception = await Assert.ThrowsAsync<AgwException>(() => service.DiscoverAsync(
            new ProviderModelDiscoveryRequest(
                ProviderType.OpenAIChatCompletions,
                "https://example.test/v1",
                "secret"),
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ProviderModelDiscoveryFailed.Code, exception.Code);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"data\":{}}")]
    public async Task DiscoverAsync_MalformedResponseShape_ThrowsDiscoveryFailed(string responseBody)
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        });

        var exception = await Assert.ThrowsAsync<AgwException>(() => service.DiscoverAsync(
            new ProviderModelDiscoveryRequest(
                ProviderType.OpenAIChatCompletions,
                "https://example.test/v1",
                "secret"),
            TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.ProviderModelDiscoveryFailed.Code, exception.Code);
    }

    private static ProviderModelDiscoveryService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        return new ProviderModelDiscoveryService(
            new StubHttpClientFactory(new StubHttpMessageHandler(handler)));
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
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
