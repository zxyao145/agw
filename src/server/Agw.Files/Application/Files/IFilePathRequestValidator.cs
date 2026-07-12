namespace Agw.Files.Application.Files;

public interface IFilePathRequestValidator
{
    FilePathRequestValidationResult ValidateRequiredPath(string? path);
}
