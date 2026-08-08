using System.Net;
using System.Text;
using System.Text.Json;

using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Agw.Tools.Impl.Web;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Tools.Tests;

public class WebSearchToolTests
{
    [Fact]
    public async Task InvokeAsync_WhenGoogleSucceeds_UsesGoogleResults()
    {
        var requestedHosts = new List<string>();
        var tool = CreateWebSearchFunction(request =>
        {
            requestedHosts.Add(request.RequestUri?.Host ?? "");

            if (request.RequestUri?.Host == "www.google.com")
            {
                return CreateResponse(
                    HttpStatusCode.OK,
                    """
                    <html><body>
                      <a href="/url?q=https%3A%2F%2Fexample.com%2Fgoogle-result&amp;sa=U">
                        <h3>Google <em>Result</em></h3>
                      </a>
                      <div class="VwiC3b">Google snippet text.</div>
                    </body></html>
                    """);
            }

            return CreateResponse(HttpStatusCode.InternalServerError, "unexpected provider");
        });

        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["query"] = "dotnet"
        }), TestContext.Current.CancellationToken);
        var resultJson = Assert.IsType<JsonElement>(result);
        var results = resultJson.GetProperty("results").EnumerateArray().ToList();

        Assert.Equal("google", resultJson.GetProperty("provider").GetString());
        Assert.Equal(1, resultJson.GetProperty("totalResults").GetInt32());
        Assert.Equal("Google Result", results[0].GetProperty("title").GetString());
        Assert.Equal("https://example.com/google-result", results[0].GetProperty("url").GetString());
        Assert.Equal("Google snippet text.", results[0].GetProperty("content").GetString());
        Assert.Equal(["www.google.com"], requestedHosts);
    }

    [Fact]
    public async Task InvokeAsync_WhenGoogleFails_UsesBingResults()
    {
        var requestedHosts = new List<string>();
        var tool = CreateWebSearchFunction(request =>
        {
            requestedHosts.Add(request.RequestUri?.Host ?? "");

            if (request.RequestUri?.Host == "www.google.com")
            {
                return CreateResponse(HttpStatusCode.InternalServerError, "google failed");
            }

            if (request.RequestUri?.Host == "www.bing.com")
            {
                return CreateResponse(
                    HttpStatusCode.OK,
                    """
                    <html><body>
                      <li class="b_algo">
                        <h2><a href="https://example.com/bing-result">Bing <strong>Result</strong></a></h2>
                        <div class="b_caption"><p>Bing snippet text.</p></div>
                      </li>
                    </body></html>
                    """);
            }

            return CreateResponse(HttpStatusCode.InternalServerError, "unexpected provider");
        });

        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["query"] = "dotnet"
        }), TestContext.Current.CancellationToken);
        var resultJson = Assert.IsType<JsonElement>(result);
        var results = resultJson.GetProperty("results").EnumerateArray().ToList();

        Assert.Equal("bing", resultJson.GetProperty("provider").GetString());
        Assert.Equal(1, resultJson.GetProperty("totalResults").GetInt32());
        Assert.Equal("Bing Result", results[0].GetProperty("title").GetString());
        Assert.Equal("https://example.com/bing-result", results[0].GetProperty("url").GetString());
        Assert.Equal("Bing snippet text.", results[0].GetProperty("content").GetString());
        Assert.Equal(["www.google.com", "www.bing.com"], requestedHosts);
    }

    [Fact]
    public async Task InvokeAsync_WhenAllSearchProvidersFail_ThrowsAgwException()
    {
        var requestedHosts = new List<string>();
        var tool = CreateWebSearchFunction(request =>
        {
            requestedHosts.Add(request.RequestUri?.Host ?? "");
            return CreateResponse(HttpStatusCode.BadGateway, "provider failed");
        });

        await Assert.ThrowsAsync<AgwException>(async () =>
            await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
            {
                ["query"] = "dotnet"
            }), TestContext.Current.CancellationToken));

        Assert.Equal(["www.google.com", "www.bing.com", "www.baidu.com"], requestedHosts);
    }

    private static AIFunction CreateWebSearchFunction(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var services = new ServiceCollection()
            .AddSingleton<IHttpClientFactory>(new StubHttpClientFactory(new StubHttpMessageHandler(handler)))
            .BuildServiceProvider();
        _ = new IocUtil(services, NullLoggerFactory.Instance);

        return Assert.IsAssignableFrom<AIFunction>(new WebSearchTool().ToAITool());
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string content)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/html")
        };
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _httpClient = new HttpClient(handler, disposeHandler: true);
        }

        public HttpClient CreateClient(string name = "")
        {
            return _httpClient;
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
