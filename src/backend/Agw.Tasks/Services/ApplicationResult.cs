namespace Agw.Tasks.Services;

public enum ApplicationResultType
{
    Success,
    NotFound,
    Invalid
}

public sealed record ApplicationResult(
    ApplicationResultType Type,
    string? Error = null)
{
    public static ApplicationResult Success() => new(ApplicationResultType.Success);

    public static ApplicationResult NotFound() => new(ApplicationResultType.NotFound);

    public static ApplicationResult Invalid(string error) => new(ApplicationResultType.Invalid, error);
}

public sealed record ApplicationResult<T>(
    ApplicationResultType Type,
    T? Value = default,
    string? Error = null)
{
    public static ApplicationResult<T> Success(T value) => new(ApplicationResultType.Success, value);

    public static ApplicationResult<T> NotFound() => new(ApplicationResultType.NotFound);

    public static ApplicationResult<T> Invalid(string error) => new(ApplicationResultType.Invalid, default, error);
}
