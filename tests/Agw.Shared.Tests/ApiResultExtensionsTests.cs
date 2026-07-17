using Agw.Shared.Exceptions;
using Agw.Shared.Results;

namespace Agw.Shared.Tests;

public sealed class ApiResultExtensionsTests
{
    [Fact]
    public void ToApiResult_ErrorCode_MapsCodeTitleAndStatusCode()
    {
        var result = ErrorCodes.ResourceNotFound.ToApiResult();

        Assert.Equal(ErrorCodes.ResourceNotFound.Code, result.Code);
        Assert.Equal(ErrorCodes.ResourceNotFound.Message, result.Title);
        Assert.Equal((int)ErrorCodes.ResourceNotFound.StatusCode, result.StatusCode);
    }

    [Fact]
    public void ToApiResult_ErrorCodeWithTitle_UsesTitleOverride()
    {
        var result = ErrorCodes.ResourceNotFound.ToApiResult("Project not found.");

        Assert.Equal("Project not found.", result.Title);
    }

    [Fact]
    public void ToApiResult_AgwException_MapsException()
    {
        var exception = new AgwException(ErrorCodes.InvalidParam, "Request is invalid.");

        var result = exception.ToApiResult();

        Assert.Equal(exception.Code, result.Code);
        Assert.Equal(exception.Message, result.Title);
        Assert.Equal((int)exception.StatusCode, result.StatusCode);
    }
}
