using Agw.Shared.Exceptions;

using Bens.Results;

namespace Agw.Shared.Results;

public static class ApiResultExtensions
{
    public static IApiResult ToApiResult(this ErrorCode errorCode, string? title = null)
    {
        return ApiResult.Fail(
            errorCode.Code,
            title ?? errorCode.Message,
            (int)errorCode.StatusCode);
    }

    public static IApiResult ToApiResult(this AgwException exception)
    {
        return ApiResult.Fail(
            exception.Code,
            exception.Message,
            (int)exception.StatusCode);
    }
}
