using System.Text;

namespace Agw.Files.Tests;

public sealed class FileReadRevisionTests : IDisposable
{
    private const string OriginalContent = "original contents";
    private const string UpdatedContent = "changed contents!";
    private static readonly DateTimeOffset ModifiedTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly string _root = Directory
        .CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "file-read-revision", Guid.CreateVersion7().ToString("N"))
        )
        .FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task GetOpenedFileRevision_InPlaceRewriteWithRestoredWriteTime_ChangesRevision()
    {
        Assert.Equal(OriginalContent.Length, UpdatedContent.Length);
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(path, OriginalContent, token);
        File.SetLastWriteTimeUtc(path, ModifiedTime.UtcDateTime);
        var creationTime = new DateTimeOffset(File.GetCreationTimeUtc(path));
        var fileSystem = TestFileSystems.Local(_root).Confine([_root]);

        string originalRevision;
        await using (var stream = await fileSystem.OpenReadAsync("notes.txt", token))
        {
            originalRevision = fileSystem.GetOpenedFileRevision(stream);
        }

        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(UpdatedContent), token);
        }
        File.SetLastWriteTimeUtc(path, ModifiedTime.UtcDateTime);

        Assert.Equal(OriginalContent.Length, new FileInfo(path).Length);
        Assert.Equal(ModifiedTime, new DateTimeOffset(File.GetLastWriteTimeUtc(path)));
        Assert.Equal(UpdatedContent, await File.ReadAllTextAsync(path, token));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(creationTime, new DateTimeOffset(File.GetCreationTimeUtc(path)));
        }

        await using var updatedStream = await fileSystem.OpenReadAsync("notes.txt", token);

        Assert.NotEqual(originalRevision, fileSystem.GetOpenedFileRevision(updatedStream));
    }
}
