using System.Net;

namespace Agw.Files.Exceptions;

public sealed class AgwFilesException : Exception
{
    public AgwFilesException(FilesErrorCode errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public AgwFilesException(FilesErrorCode errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public FilesErrorCode ErrorCode { get; }

    public int Code => (int)ErrorCode;

    public HttpStatusCode StatusCode => ErrorCode switch
    {
        FilesErrorCode.InvalidParameter => HttpStatusCode.BadRequest,
        FilesErrorCode.PathOutsideRoot => HttpStatusCode.Forbidden,
        FilesErrorCode.InvalidStorageConfiguration => HttpStatusCode.InternalServerError,
        FilesErrorCode.UnsupportedStorageBackend => HttpStatusCode.NotImplemented,
        _ => HttpStatusCode.InternalServerError
    };
}
