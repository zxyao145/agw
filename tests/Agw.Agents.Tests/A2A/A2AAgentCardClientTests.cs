using global::A2A;
using Agw.Agents.Application.A2A;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Agents.Tests.A2A;

public sealed class A2AAgentCardClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData("?v=2")]
    [InlineData("/?v=2")]
    public async Task FetchAsync_ValidCard_RequestsExactUrlAndSendsOriginCredentials(string suffix)
    {
        var headers = new Dictionary<string, string> { ["X-Api-Key"] = "card-secret" };
        await using var server = await StartAsync(headers);
        await using var services = CreateServices();
        var url = new Uri(server.CardUrl.AbsoluteUri + suffix);
        var other = new Uri("https://other.example");
        var card = await services
            .GetRequiredService<IA2AAgentCardClient>()
            .FetchAsync(
                url,
                new A2AEndpointCredentials([
                    KeyValuePair.Create(A2AEndpointRules.NormalizeOrigin(url), headers),
                    KeyValuePair.Create(
                        A2AEndpointRules.NormalizeOrigin(other),
                        new Dictionary<string, string> { ["X-Other-Key"] = "other-secret" }
                    ),
                ]),
                Ct
            );

        Assert.Equal("test-agent", card.Card.GetProperty("name").GetString());
        Assert.Equal(server.InterfaceUrl.AbsoluteUri, card.Interface.Url);
        var request = Assert.Single(server.Requests);
        Assert.Equal(url.PathAndQuery, request.PathAndQuery);
        Assert.Equal("card-secret", request.Headers["X-Api-Key"]);
        Assert.DoesNotContain("X-Other-Key", request.Headers.Keys);
    }

    [Fact]
    public async Task FetchAsync_ConcurrentRequestsOnSameOrigin_UseTheirOwnCredentials()
    {
        await using var server = await StartAsync();
        server.CardDelay = TimeSpan.FromMilliseconds(100);
        await using var services = CreateServices();
        var client = services.GetRequiredService<IA2AAgentCardClient>();
        var first = new Uri(server.CardUrl.AbsoluteUri + "?request=first");
        var second = new Uri(server.CardUrl.AbsoluteUri + "?request=second");

        await Task.WhenAll(
            client.FetchAsync(first, Credentials(first, "Authorization", "Bearer first"), Ct),
            client.FetchAsync(second, Credentials(second, "Authorization", "Bearer second"), Ct)
        );

        Assert.Equal(2, server.Requests.Count);
        var requests = server.Requests.ToDictionary(request => request.PathAndQuery);
        Assert.Equal("Bearer first", requests[first.PathAndQuery].Headers["Authorization"]);
        Assert.Equal("Bearer second", requests[second.PathAndQuery].Headers["Authorization"]);
    }

    [Fact]
    public async Task FetchAsync_Unauthorized_ThrowsFetchFailed()
    {
        await using var server = await StartAsync(new Dictionary<string, string> { ["Authorization"] = "Bearer card" });
        await using var services = CreateServices();

        var error = await FetchErrorAsync(services, server.CardUrl);

        Assert.Equal(ErrorCodes.A2AAgentCardFetchFailed.Code, error.Code);
        Assert.Contains("401", error.Message);
    }

    [Theory]
    [InlineData("<html></html>")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task FetchAsync_MalformedCard_ThrowsCardInvalid(string body)
    {
        await using var server = await StartAsync();
        server.CardBodyOverride = body;
        await using var services = CreateServices();

        Assert.Equal(ErrorCodes.A2AAgentCardInvalid.Code, (await FetchErrorAsync(services, server.CardUrl)).Code);
    }

    [Fact]
    public async Task FetchAsync_InvalidJson_PreservesSafeExceptionStack()
    {
        await using var server = await StartAsync();
        server.CardBodyOverride = "{";
        await using var services = CreateServices();

        var error = await FetchErrorAsync(services, server.CardUrl);

        var cause = Assert.IsAssignableFrom<Exception>(error.InnerException);
        Assert.Contains(nameof(A2ACardResolver), cause.StackTrace);
    }

    [Fact]
    public async Task FetchAsync_CardLargerThanLimit_ThrowsCardInvalid()
    {
        await using var server = await StartAsync();
        server.Card.Description = new string('x', 4096);
        await using var services = CreateServices(("CardMaxBytes", "1024"));

        Assert.Equal(ErrorCodes.A2AAgentCardInvalid.Code, (await FetchErrorAsync(services, server.CardUrl)).Code);
    }

    [Fact]
    public async Task FetchAsync_ServerSlowerThanTimeout_ThrowsFetchFailed()
    {
        await using var server = await StartAsync();
        server.CardDelay = TimeSpan.FromSeconds(3);
        await using var services = CreateServices(("CardFetchTimeout", "00:00:01"));

        Assert.Equal(ErrorCodes.A2AAgentCardFetchFailed.Code, (await FetchErrorAsync(services, server.CardUrl)).Code);
    }

    [Fact]
    public async Task FetchAsync_PrivateNetworksDisallowed_RejectsLoopbackTarget()
    {
        await using var server = await StartAsync();
        await using var services = CreateServices(("AllowPrivateNetworks", "false"));

        Assert.Equal(ErrorCodes.InvalidParam.Code, (await FetchErrorAsync(services, server.CardUrl)).Code);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task FetchAsync_RedirectResponse_ThrowsFetchFailed()
    {
        await using var server = await StartAsync();
        await using var target = await StartAsync();
        server.RedirectLocation = target.CardUrl.AbsoluteUri;
        await using var services = CreateServices();

        var error = await FetchErrorAsync(
            services,
            server.CardUrl,
            Credentials(server.CardUrl, "Authorization", "Bearer card")
        );

        Assert.Equal(ErrorCodes.A2AAgentCardFetchFailed.Code, error.Code);
        Assert.Contains("302", error.Message);
        Assert.Single(server.Requests);
        Assert.Empty(target.Requests);
    }

    [Fact]
    public async Task RemoteHttpClient_SharedHandler_DoesNotShareCookiesBetweenClients()
    {
        await using var server = await StartAsync();
        await using var services = CreateServices();
        var client = services.GetRequiredService<IA2AAgentCardClient>();
        server.SetCookie = "session=first; Path=/";
        await client.FetchAsync(server.CardUrl, Credentials(server.CardUrl, "X-Tenant", "first"), Ct);
        server.SetCookie = null;
        await client.FetchAsync(server.CardUrl, Credentials(server.CardUrl, "X-Tenant", "second"), Ct);

        Assert.Equal(2, server.Requests.Count);
        Assert.DoesNotContain("Cookie", server.Requests[1].Headers.Keys);
    }

    [Fact]
    public async Task RemoteHttpClient_ExplicitCookieHeader_UsesOnlyItsOrigin()
    {
        await using var server = await StartAsync();
        await using var other = await StartAsync();
        await using var services = CreateServices();
        var credentials = Credentials(server.CardUrl, "Cookie", "session=explicit");
        var client = services.GetRequiredService<IA2AAgentCardClient>();

        await client.FetchAsync(server.CardUrl, credentials, Ct);
        await client.FetchAsync(other.CardUrl, credentials, Ct);

        Assert.Equal("session=explicit", Assert.Single(server.Requests).Headers["Cookie"]);
        Assert.DoesNotContain("Cookie", Assert.Single(other.Requests).Headers.Keys);

        using var bound = services
            .GetRequiredService<IA2ARemoteHttpClientFactory>()
            .Create(server.CardUrl, credentials);
        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            (
                await Assert.ThrowsAsync<AgwException>(async () =>
                    await bound.InvokeAsync(() => bound.Client.GetAsync(other.CardUrl, Ct))
                )
            ).Code
        );
        Assert.Single(other.Requests);
    }

    internal static ServiceProvider CreateServices(params (string Key, string Value)[] settings) =>
        new ServiceCollection()
            .AddLogging()
            .AddAgents(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(
                        settings.ToDictionary(
                            setting => $"A2ARemoteAgents:{setting.Key}",
                            setting => (string?)setting.Value
                        )
                    )
                    .Build()
            )
            .BuildServiceProvider();

    internal static A2AEndpointCredentials Credentials(Uri url, string name, string value) =>
        new([
            KeyValuePair.Create(
                A2AEndpointRules.NormalizeOrigin(url),
                new Dictionary<string, string> { [name] = value }
            ),
        ]);

    private static Task<A2ATestAgentServer> StartAsync(IReadOnlyDictionary<string, string>? headers = null) =>
        A2ATestAgentServer.StartAsync(
            new(ProtocolBindingNames.HttpJson, true, ScriptedAgentHandler.Echo()) { RequiredCardHeaders = headers },
            Ct
        );

    private static Task<AgwException> FetchErrorAsync(
        ServiceProvider services,
        Uri url,
        A2AEndpointCredentials? credentials = null
    ) =>
        Assert.ThrowsAsync<AgwException>(() =>
            services
                .GetRequiredService<IA2AAgentCardClient>()
                .FetchAsync(url, credentials ?? A2AEndpointCredentials.Empty, Ct)
        );
}
