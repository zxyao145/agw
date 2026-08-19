using System.Reflection;
using Agw.Shared.Results;
using Agw.Shared.Runtime;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Host.Controllers;

[ApiController]
[Route("api/server-info")]
[Tags("agw-server")]
public sealed class ServerInfoController : ControllerBase
{
    private readonly IServerInitializationState _initializationState;

    public ServerInfoController(IServerInitializationState initializationState)
    {
        _initializationState = initializationState;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ServerInfoResponse))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        var version =
            Assembly
                .GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "0.0.0-dev";
        return ApiResult.Ok(new ServerInfoResponse(version, 1, _initializationState.IsInitialized));
    }

    public sealed record ServerInfoResponse(string ServerVersion, int ApiMajorVersion, bool Initialized);
}
