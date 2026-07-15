using Agw.Files.Utils;

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
