using global::A2A;
using Agw.Agents.Definitions.Domain.Rules;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Tests.A2A;

public sealed class A2AEndpointRulesTests
{
    [Theory]
    [InlineData("ftp://example.com/card.json")]
    [InlineData("/relative/card.json")]
    [InlineData("https://user:pass@example.com/card.json")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateCardUrl_Invalid_ThrowsInvalidParam(string? cardUrl) =>
        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            Assert.Throws<AgwException>(() => A2AEndpointRules.ValidateCardUrl(cardUrl)).Code
        );

    [Fact]
    public void ValidateCardUrl_LongerThan2048_ThrowsInvalidParam() =>
        ValidateCardUrl_Invalid_ThrowsInvalidParam($"https://example.com/{new string('x', 2048)}");

    [Theory]
    [InlineData("HTTPS://A.EXAMPLE/x", "https://a.example:443")]
    [InlineData("https://a.example:443/y", "https://a.example:443")]
    [InlineData("https://a.example:8443/x", "https://a.example:8443")]
    [InlineData("https://BÜCHER.example/path", "https://xn--bcher-kva.example:443")]
    [InlineData("http://[::1]/path", "http://[::1]:80")]
    public void NormalizeOrigin_ValidAddress_ReturnsCanonicalOrigin(string url, string origin) =>
        Assert.Equal(origin, A2AEndpointRules.NormalizeOrigin(new Uri(url)));

    [Theory]
    [InlineData("https://user@a.example")]
    [InlineData("https://a.example/path")]
    [InlineData("https://a.example/?query")]
    [InlineData("https://a.example/#fragment")]
    [InlineData("ftp://a.example")]
    public void ResolveCredentials_InvalidOrigin_ThrowsInvalidParam(string origin) =>
        AssertInvalid([new(origin, new Dictionary<string, string?>())]);

    [Fact]
    public void ResolveCredentials_MoreThanTwoOrigins_ThrowsInvalidParam() =>
        AssertInvalid(
            new[] { "https://a.example", "https://b.example", "https://c.example" }
                .Select(origin => new A2AOriginCredentialsInput(origin, new Dictionary<string, string?>()))
                .ToArray()
        );

    [Fact]
    public void ResolveCredentials_EquivalentOrigins_ThrowsInvalidParam() =>
        AssertInvalid([
            new("https://a.example", new Dictionary<string, string?>()),
            new("HTTPS://A.EXAMPLE:443/", new Dictionary<string, string?>()),
        ]);

    [Theory]
    [InlineData("Host", "value")]
    [InlineData("Content-Length", "value")]
    [InlineData("Content-Type", "value")]
    [InlineData("Transfer-Encoding", "value")]
    [InlineData("Connection", "value")]
    [InlineData("Accept", "value")]
    [InlineData("A2A-Version", "value")]
    [InlineData("A2A-Extensions", "value")]
    [InlineData("X Bad", "value")]
    [InlineData("", "value")]
    [InlineData("Authorization", "")]
    [InlineData("Authorization", "value\r\nextra")]
    public void ResolveCredentials_InvalidHeader_ThrowsInvalidParam(string name, string value) =>
        AssertInvalid([new("https://a.example", new Dictionary<string, string?> { [name] = value })]);

    [Fact]
    public void ResolveCredentials_HeaderTooLong_ThrowsInvalidParam() =>
        ResolveCredentials_InvalidHeader_ThrowsInvalidParam("Authorization", new string('a', 8193));

    [Fact]
    public void ResolveCredentials_TooManyHeaders_ThrowsInvalidParam() =>
        AssertInvalid([
            new("https://a.example", Enumerable.Range(1, 21).ToDictionary(i => $"X-{i}", _ => (string?)"value")),
        ]);

    [Fact]
    public void ResolveCredentials_CaseInsensitiveDuplicateHeader_ThrowsInvalidParam() =>
        AssertInvalid([
            new(
                "https://a.example",
                new Dictionary<string, string?> { ["Authorization"] = "a", ["authorization"] = "b" }
            ),
        ]);

    [Fact]
    public void ResolveCredentials_NullValue_KeepsValueForSameOrigin()
    {
        var stored = new Dictionary<string, Dictionary<string, string>>
        {
            ["https://a.example:443"] = new() { ["Authorization"] = "a", ["Cookie"] = "removed" },
            ["https://b.example:443"] = new() { ["Authorization"] = "b" },
        };

        var credentials = A2AEndpointRules.ResolveCredentials(
            new A2AEndpointCredentials(stored),
            [
                new(
                    "HTTPS://A.EXAMPLE/",
                    new Dictionary<string, string?> { ["authorization"] = null, ["X-Tenant"] = "blue" }
                ),
            ]
        );

        var origin = Assert.Single(credentials.HeadersByOrigin);
        Assert.Equal("https://a.example:443", origin.Key);
        Assert.Equal("a", origin.Value["Authorization"]);
        Assert.Equal("blue", origin.Value["X-Tenant"]);
        Assert.Equal(2, origin.Value.Count);
    }

    [Theory]
    [InlineData("https://a.example", "X-Missing")]
    [InlineData("https://b.example", "Authorization")]
    public void ResolveCredentials_NullValueWithoutStoredMatch_ThrowsInvalidParam(string origin, string header)
    {
        var stored = new Dictionary<string, Dictionary<string, string>>
        {
            ["https://a.example:443"] = new() { ["Authorization"] = "a" },
        };
        var error = Assert.Throws<AgwException>(() =>
            A2AEndpointRules.ResolveCredentials(
                new A2AEndpointCredentials(stored),
                [new(origin, new Dictionary<string, string?> { [header] = null })]
            )
        );
        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }

    [Fact]
    public void SelectInterface_MultipleSupported_ReturnsFirstInCardOrder()
    {
        Assert.Equal(
            new A2ASelectedInterface("https://a.example/rpc", ProtocolBindingNames.JsonRpc),
            A2AEndpointRules.SelectInterface(
                [
                    ("https://a.example/grpc", "GRPC"),
                    ("https://a.example/rpc", "jsonrpc"),
                    ("https://a.example/rest", ProtocolBindingNames.HttpJson),
                ],
                [ProtocolBindingNames.HttpJson, ProtocolBindingNames.JsonRpc]
            )
        );
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("https://a.example/agent", "GRPC")]
    [InlineData("/agent", "HTTP+JSON")]
    public void SelectInterface_NoUsableInterface_ThrowsCardInvalid(string? url, string? binding)
    {
        (string Url, string ProtocolBinding)[] candidates = url is null ? [] : [(url, binding!)];
        Assert.Equal(
            ErrorCodes.A2AAgentCardInvalid.Code,
            Assert
                .Throws<AgwException>(() =>
                    A2AEndpointRules.SelectInterface(
                        candidates,
                        [ProtocolBindingNames.HttpJson, ProtocolBindingNames.JsonRpc]
                    )
                )
                .Code
        );
    }

    private static void AssertInvalid(IReadOnlyList<A2AOriginCredentialsInput> credentials) =>
        Assert.Equal(
            ErrorCodes.InvalidParam.Code,
            Assert.Throws<AgwException>(() => A2AEndpointRules.ResolveCredentials(null, credentials)).Code
        );
}
