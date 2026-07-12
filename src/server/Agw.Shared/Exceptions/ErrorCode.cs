using System.Net;

namespace Agw.Shared.Exceptions;

public record ErrorCode(int Code, string Message, HttpStatusCode StatusCode);
