using System.Net;

using Agw.Files.Exceptions;

namespace Agw.Files.Tests;

public class AgwFilesExceptionTests
{
    [Theory]
    [InlineData(FilesErrorCode.InvalidParameter, 400_0001, HttpStatusCode.BadRequest)]
    [InlineData(FilesErrorCode.PathOutsideRoot, 403_0001, HttpStatusCode.Forbidden)]
    [InlineData(FilesErrorCode.InvalidStorageConfiguration, 500_0014, HttpStatusCode.InternalServerError)]
    [InlineData(FilesErrorCode.UnsupportedStorageBackend, 501_0008, HttpStatusCode.NotImplemented)]
    public void Constructor_UsesStableCodeAndHttpStatus(
        FilesErrorCode errorCode,
        int expectedCode,
        HttpStatusCode expectedStatusCode)
    {
        var exception = new AgwFilesException(errorCode, "Failure");

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(errorCode, exception.ErrorCode);
        Assert.Equal(expectedStatusCode, exception.StatusCode);
    }
}
