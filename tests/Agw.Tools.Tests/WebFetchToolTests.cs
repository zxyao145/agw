using System.Net;
using System.Text.Json;
using Agw.Tools.Impl.Tools.Web;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Tools.Tests;

public sealed class WebFetchToolTests
{
    [Fact]
    public async Task InvokeAsync_FetchesContentUsingInjectedHttpClientFactory()
    {
        HttpRequestMessage? request = null;
        var tool = new WebFetchTool(
            new StubHttpClientFactory(
                new StubHttpMessageHandler(message =>
                {
                    request = message;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("page content") };
                })
            ),
            NullLogger<WebFetchTool>.Instance
        );

        var function = Assert.IsAssignableFrom<AIFunction>(tool.ToAITool());
        var result = await function.InvokeAsync(
            new AIFunctionArguments { ["url"] = "https://example.com/page", ["prompt"] = "" },
            TestContext.Current.CancellationToken
        );

        var resultJson = Assert.IsType<JsonElement>(result);
        Assert.Equal("https://example.com/page", request?.RequestUri?.AbsoluteUri);
        Assert.Equal("page content", resultJson.GetProperty("result").GetString());
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name = "") => new(_handler);
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
        ) => Task.FromResult(_handler(request));
    }
}
