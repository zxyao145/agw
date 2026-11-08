using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Shared.Exceptions;
using Xunit;

namespace Agw.Extensions.Tests;

public sealed class ExtensionManifestTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Parse_SupportedTargets_PreservesFlags(int targets)
    {
        var manifest = ExtensionManifestReader.Parse(CreateManifest(targets));
        Assert.Equal((ExtensionTarget)targets, manifest.Targets);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(-1)]
    public void Parse_UnsupportedTargets_RejectsManifest(int targets)
    {
        Assert.Throws<AgwException>(() => ExtensionManifestReader.Parse(CreateManifest(targets)));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    public void Parse_InvalidSemVer_RejectsManifest(string version)
    {
        Assert.Throws<AgwException>(() => ExtensionManifestReader.Parse(CreateManifest(1, version)));
    }

    [Fact]
    public void Parse_DesktopV1_PreservesIdentityAndDisplayName()
    {
        var json =
            """{"manifestVersion":1,"id":"019a4c17-5f00-7a80-8b3d-2b732f48a100","name":"预约管理","version":"1.0.0","apiVersion":1,"contributes":{}}""";
        var manifest = ExtensionManifestReader.Parse(json);
        Assert.Equal("预约管理", manifest.DisplayName);
        Assert.Equal(ExtensionTarget.Desktop, manifest.Targets);
        Assert.Equal("019a4c17-5f00-7a80-8b3d-2b732f48a100", manifest.Id);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("files")]
    public void Parse_NullRequiredField_RejectsManifest(string field)
    {
        var value = System.Text.Json.Nodes.JsonNode.Parse(CreateManifest(1))!;
        value[field] = null;
        Assert.Throws<AgwException>(() => ExtensionManifestReader.Parse(value.ToJsonString()));
    }

    [Theory]
    [InlineData("../extension.dll")]
    [InlineData("/extension.dll")]
    [InlineData("server\\extension.dll")]
    [InlineData("server/%2e%2e/extension.dll")]
    public void ValidatePath_UnsafePath_RejectsPath(string path)
    {
        Assert.Throws<AgwException>(() => ExtensionManifestReader.ValidatePath(path));
    }

    private static string CreateManifest(int targets, string version = "1.0.0") =>
        JsonSerializer.Serialize(
            new
            {
                manifestVersion = 2,
                id = "019a4c17-5f00-7a80-8b3d-2b732f48a100",
                name = "calendar",
                displayName = "预约管理",
                version,
                targets,
                server = (targets & 1) != 0
                    ? new
                    {
                        apiVersion = 1,
                        entryAssembly = "server/calendar.dll",
                        entryType = "Calendar.Entry",
                    }
                    : null,
                desktop = (targets & 2) != 0 ? new { apiVersion = 1, contributes = new { } } : null,
                files = new Dictionary<string, string>(),
            }
        );
}
