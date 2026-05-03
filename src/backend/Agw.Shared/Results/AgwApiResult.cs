using Agw.Shared.Exceptions;

using Bens.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Shared.Results;

public static class AgwApiResult
{
    public static IActionResult Ok()
    {
        return ApiResult.Ok();
    }

    public static IActionResult Ok<T>(T data)
    {
        return ApiResult.Ok(data);
    }

    public static IActionResult BadRequest(string title)
    {
        return ApiResult.BadRequest(title, ErrorCodes.InvalidParam.Code);
    }

    public static IActionResult NotFound()
    {
        return FromError(ErrorCodes.ResourceNotFound);
    }

    public static IActionResult Fail(AgwException exception)
    {
        return ApiResult.Fail(exception.Code, exception.Message, (int)exception.StatusCode);
    }

    public static IActionResult FromError(ErrorCode errorCode, string? title = null)
    {
        return ApiResult.Fail(errorCode.Code, title ?? errorCode.Message, (int)errorCode.StatusCode);
    }
}
