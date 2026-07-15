using Agw.Files.Application.Storage.Sftp;
using Agw.Files.Exceptions;

using Renci.SshNet;

namespace Agw.Files.Tests;

public class SftpFileSystemPathSecurityTests
{
    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../secret.txt")]
    [InlineData("nested/../../secret.txt")]
    public async Task ExistsFileAsync_PathOutsideRoot_ThrowsPathOutsideRoot(string path)
    {
        await using var fileSystem = new SftpFileSystem(
            new SftpClient("localhost", "test", "test"),
            "/workspace");

        var exception = await Assert.ThrowsAsync<AgwFilesException>(
            () => fileSystem.ExistsFileAsync(path, TestContext.Current.CancellationToken));

        Assert.Equal(FilesErrorCode.PathOutsideRoot, exception.ErrorCode);
    }
}
