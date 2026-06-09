using Agw.Shared.Contracts.Storage;
using Agw.Shared.Utils;

namespace Agw.Files.Application.Storage.Local;

public sealed class LocalFileSystemFactory
{
    public LocalFileSystem Create(LocalFileStorageOptions options)
    {
        return new LocalFileSystem(options.RootPath);
    }

    public LocalFileSystem Create(string rootPath)
    {
        rootPath = PathUtil.ExpandTilde(rootPath);
        return new LocalFileSystem(rootPath);
    }
}
