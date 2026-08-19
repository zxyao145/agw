using System.Net;

namespace Agw.Shared.Exceptions;

public class AgwException : Exception
{
    private const int DEFAULT_CODE = -1;

    public int Code { get; }

    public HttpStatusCode StatusCode { get; }

    public AgwException(string message)
        : this(DEFAULT_CODE, message, HttpStatusCode.BadRequest) { }

    public AgwException(int code, string message)
        : this(code, message, HttpStatusCode.BadRequest) { }

    public AgwException(int code, string message, HttpStatusCode statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public AgwException(int code, string message, HttpStatusCode statusCode, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public AgwException(ErrorCode errorCode)
        : this(errorCode.Code, errorCode.Message, errorCode.StatusCode) { }

    public AgwException(ErrorCode errorCode, string message)
        : this(errorCode.Code, message, errorCode.StatusCode) { }

    public AgwException(ErrorCode errorCode, string message, Exception innerException)
        : this(errorCode.Code, message, errorCode.StatusCode, innerException) { }
}
