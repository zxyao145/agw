namespace Agw.Files.Application.Files;

public sealed record FilePathRequestValidationResult(
    bool IsValid,
    string ResolvedPath,
    string? ErrorMessage)
{
    public static FilePathRequestValidationResult Success(string resolvedPath)
    {
        return new FilePathRequestValidationResult(true, resolvedPath, null);
    }

    public static FilePathRequestValidationResult Error(string errorMessage)
    {
        return new FilePathRequestValidationResult(false, string.Empty, errorMessage);
    }
}
