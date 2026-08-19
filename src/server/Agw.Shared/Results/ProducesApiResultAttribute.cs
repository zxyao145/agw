using Bens.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Shared.Results;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class ProducesApiResultAttribute : ProducesResponseTypeAttribute
{
    public ProducesApiResultAttribute()
        : this(StatusCodes.Status200OK) { }

    public ProducesApiResultAttribute(int statusCode)
        : base(typeof(ApiResult), statusCode) { }

    public ProducesApiResultAttribute(Type dataType)
        : this(dataType, StatusCodes.Status200OK) { }

    public ProducesApiResultAttribute(Type dataType, int statusCode)
        : base(typeof(ApiResult<>).MakeGenericType(dataType), statusCode) { }
}
