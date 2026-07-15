using System.Text.Json;

using Agw.Integrations.Domain.Plugins;

namespace Agw.Integrations.Tests;

public class AuthSchemeTypeJsonTests
{
    [Theory]
    [InlineData(AuthSchemeType.OAuth2, "\"OAuth2\"")]
    [InlineData(AuthSchemeType.ApiKey, "\"ApiKey\"")]
    [InlineData(AuthSchemeType.AkSk, "\"AkSk\"")]
    public void JsonSerialization_RoundTripsEnumName(AuthSchemeType value, string expectedJson)
    {
        var json = JsonSerializer.Serialize(value);
        var deserialized = JsonSerializer.Deserialize<AuthSchemeType>(json);

        Assert.Equal(expectedJson, json);
        Assert.Equal(value, deserialized);
    }
}
