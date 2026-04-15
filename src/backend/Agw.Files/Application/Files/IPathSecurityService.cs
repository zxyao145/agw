namespace Agw.Files.Application.Files;

public interface IPathSecurityService
{
    string RootPath { get; }

    bool TryResolvePath(string path, out string resolvedPath);
}
