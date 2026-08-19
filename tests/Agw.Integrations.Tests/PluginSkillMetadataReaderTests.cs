using Agw.Integrations.Application.Capabilities;
using Agw.Integrations.Domain.Plugins;

namespace Agw.Integrations.Tests;

public sealed class PluginSkillMetadataReaderTests
{
    [Fact]
    public void TryRead_BuiltInSkill_ReturnsFrontmatterMetadata()
    {
        var reader = new PluginSkillMetadataReader(new AppContextPluginContentRootProvider());

        var result = reader.TryRead(
            new PluginSkillDefinition { ContentPath = "Plugins/github/skills/github/SKILL.md" },
            out var metadata
        );

        Assert.True(result);
        Assert.Equal("github", metadata.Id);
        Assert.Equal("Use connected GitHub tools to inspect and work with repositories.", metadata.Description);
        Assert.EndsWith("SKILL.md", metadata.SkillFilePath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("---\nname: test-skill\n---\n", false)]
    [InlineData("---\nname: invalid_name\ndescription: Invalid name.\n---\n", false)]
    [InlineData("---\nname: test-skill\ndescription: \"Use a test: safely.\"\n---\n", true)]
    public void TryRead_Frontmatter_ReturnsExpectedResult(string content, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), $"agw-plugin-skill-{Guid.CreateVersion7():N}");
        var directory = Path.Combine(root, "skills", "test");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
            var reader = new PluginSkillMetadataReader(new FixedPluginContentRootProvider(root));

            var result = reader.TryRead(
                new PluginSkillDefinition { ContentPath = "skills/test/SKILL.md" },
                out var metadata
            );

            Assert.Equal(expected, result);
            if (expected)
            {
                Assert.Equal("test-skill", metadata.Id);
                Assert.Equal("Use a test: safely.", metadata.Description);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
