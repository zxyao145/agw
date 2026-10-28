namespace Agw.Files.Tests;

public sealed class TempGitRepositoryTests
{
    [Fact]
    public void Dispose_ReadOnlyGitObject_RemovesRepository()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("tracked.txt", "content\n");
        repository.CommitAll();

        var objectsPath = repository.GetPath(Path.Combine(".git", "objects"));
        var objectFiles = Directory.EnumerateFiles(objectsPath, "*", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(objectFiles);
        var objectPath = objectFiles[0];
        File.SetAttributes(objectPath, File.GetAttributes(objectPath) | FileAttributes.ReadOnly);
        Assert.True((File.GetAttributes(objectPath) & FileAttributes.ReadOnly) != 0);
        var repositoryPath = repository.Path;

        repository.Dispose();

        Assert.False(Directory.Exists(repositoryPath));
    }
}
