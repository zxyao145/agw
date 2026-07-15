namespace Agw.Files.Application.Files;

public enum FileOperationStatus
{
    Success,
    NotFound,
    InvalidRequest,
    Failure
}

public sealed record FileOperationResult<T>(
    FileOperationStatus Status,
    T? Value,
    string? Message,
    string? Details)
{
    public static FileOperationResult<T> Succeeded(T value)
    {
        return new FileOperationResult<T>(FileOperationStatus.Success, value, null, null);
    }

    public static FileOperationResult<T> Missing(string message)
    {
        return new FileOperationResult<T>(FileOperationStatus.NotFound, default, message, null);
    }

    public static FileOperationResult<T> Invalid(string message, string? details = null)
    {
        return new FileOperationResult<T>(FileOperationStatus.InvalidRequest, default, message, details);
    }

    public static FileOperationResult<T> Failed(string message, string? details = null)
    {
        return new FileOperationResult<T>(FileOperationStatus.Failure, default, message, details);
    }
}
