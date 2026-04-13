namespace Agw.Tasks.Application.Files;

public interface IPathSecurityService
{
    string RootPath { get; }

    bool TryResolvePath(string path, out string resolvedPath);
}
