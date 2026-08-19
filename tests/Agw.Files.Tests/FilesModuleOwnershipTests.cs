using Agw.Files.Api;
using Agw.Files.Application.Files;

namespace Agw.Files.Tests;

public class FilesModuleOwnershipTests
{
    [Fact]
    public void FilesController_LivesInAgwFilesAssembly()
    {
        Assert.Equal("Agw.Files", typeof(FilesController).Assembly.GetName().Name);
    }

    [Fact]
    public void AgwFilesAssembly_DoesNotReferenceAgwShared()
    {
        var referencedAssemblies = typeof(FilesController).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referencedAssemblies, assembly => assembly.Name == "Agw.Shared");
    }

    [Fact]
    public void AgwFilesAssembly_DoesNotExposePathSecurityService()
    {
        var assembly = typeof(FilesController).Assembly;

        Assert.Null(assembly.GetType("Agw.Files.Application.Files.IPathSecurityService"));
        Assert.Null(assembly.GetType("Agw.Files.Application.Files.PathSecurityService"));
        Assert.Null(assembly.GetType("Agw.Files.Application.Files.IFilePathRequestValidator"));
        Assert.Null(assembly.GetType("Agw.Files.Application.Files.FilePathRequestValidator"));
    }

    [Fact]
    public void FilesController_DependsOnlyOnApplicationService()
    {
        var constructor = Assert.Single(typeof(FilesController).GetConstructors());
        var parameterTypes = constructor.GetParameters().Select(parameter => parameter.ParameterType).ToArray();

        Assert.Equal([typeof(FileAppService)], parameterTypes);
    }

    [Theory]
    [InlineData("Agw.Files.Api.Dtos.FileListResponse")]
    [InlineData("Agw.Files.Abstracts.IAgwFileSystem")]
    [InlineData("Agw.Files.Abstracts.IAgwFileSystemResolver")]
    [InlineData("Agw.Files.Abstracts.IProjectFileSystemConfigurationProvider")]
    [InlineData("Agw.Files.Services.IGitCommandService")]
    [InlineData("Agw.Files.Utils.PathUtil")]
    [InlineData("Agw.Files.Exceptions.AgwFilesException")]
    public void SdkType_LivesInAgwFilesAssembly(string typeName)
    {
        var sdkType = typeof(FilesController).Assembly.GetType(typeName);

        Assert.NotNull(sdkType);
    }

    [Theory]
    [InlineData("Agw.Files.FileStorageOptions")]
    [InlineData("Agw.Files.LocalFileStorageOptions")]
    [InlineData("Agw.Files.SftpFileStorageOptions")]
    [InlineData("Agw.Files.FileStorageType")]
    [InlineData("Agw.Files.Application.Storage.Local.LocalFileSystemFactory")]
    [InlineData("Agw.Files.Application.Storage.Sftp.SftpFileSystem")]
    [InlineData("Agw.Files.Application.Storage.Sftp.SftpFileSystemFactory")]
    public void RemovedStorageSdkType_IsAbsent(string typeName)
    {
        var sdkType = typeof(FilesController).Assembly.GetType(typeName);

        Assert.Null(sdkType);
    }
}
