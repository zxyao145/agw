namespace Agw.Files.Application.Files;

public sealed class FilePathRequestValidator : IFilePathRequestValidator
{
    private readonly IPathSecurityService _pathSecurityService;

    public FilePathRequestValidator(IPathSecurityService pathSecurityService)
    {
        _pathSecurityService = pathSecurityService;
    }

    public FilePathRequestValidationResult ValidateRequiredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return FilePathRequestValidationResult.Error("Path parameter is required");
        }

        return _pathSecurityService.TryResolvePath(path, out var resolvedPath)
            ? FilePathRequestValidationResult.Success(resolvedPath)
            : FilePathRequestValidationResult.Error("Invalid path");
    }
}
