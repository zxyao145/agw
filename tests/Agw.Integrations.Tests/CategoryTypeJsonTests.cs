using System.Text.Json;
using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Integrations.Tests;

public class CategoryTypeJsonTests
{
    [Fact]
    public void JsonSerialization_RoundTripsEnumName()
    {
        var json = JsonSerializer.Serialize(CategoryType.GitServer);
        var value = JsonSerializer.Deserialize<CategoryType>(json);

        Assert.Equal("\"GitServer\"", json);
        Assert.Equal(CategoryType.GitServer, value);
    }
}
