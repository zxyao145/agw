using Agw.Host.Runtime;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agw.Host.Tests;

public sealed class ExtensionServerAddressTests
{
    [Theory]
    [InlineData("https://agw.example.com/", "https://agw.example.com")]
    [InlineData("http://127.0.0.1:31983", "http://127.0.0.1:31983")]
    public void GetPublicBaseUrl_ValidConfiguredAddress_ReturnsCanonicalAddress(string value, string expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Extensions:PublicBaseUrl"] = value })
            .Build();
        Assert.Equal(expected, new ExtensionServerAddress(configuration).GetPublicBaseUrl());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://agw.example.com")]
    [InlineData("https://user:secret@agw.example.com")]
    [InlineData("https://agw.example.com?redirect=other")]
    public void GetPublicBaseUrl_InvalidConfiguredAddress_ThrowsInvalidParam(string? value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Extensions:PublicBaseUrl"] = value })
            .Build();
        var error = Assert.Throws<AgwException>(() => new ExtensionServerAddress(configuration).GetPublicBaseUrl());
        Assert.Equal(ErrorCodes.InvalidParam.Code, error.Code);
    }
}
