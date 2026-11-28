using System.IO.Compression;
using Microsoft.AspNetCore.Http;

namespace Agw.Skills.Tests;

internal static class SkillTestArchives
{
    internal static IFormFile CreateArchive()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("test-skill/SKILL.md").Open()))
        {
            writer.Write("---\nname: test-skill\ndescription: description\n---\nInstructions");
        }
        stream.Position = 0;
        return new FormFile(stream, 0, stream.Length, "archive", "skill.zip");
    }

    internal static IFormFile CreateArchive(
        string instructions,
        string nameKey = "name",
        string nameValue = "test-skill",
        string? additionalFrontmatter = null
    )
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("test-skill/SKILL.md").Open()))
        {
            var additionalFrontmatterLines = additionalFrontmatter is null
                ? string.Empty
                : $"{additionalFrontmatter}\n";
            writer.Write(
                $"---\n{nameKey}: {nameValue}\n{additionalFrontmatterLines}description: description\n---\n{instructions}"
            );
        }
        stream.Position = 0;
        return new FormFile(stream, 0, stream.Length, "archive", "skill.zip");
    }
}
