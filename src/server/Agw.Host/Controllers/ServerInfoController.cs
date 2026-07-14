using System.Reflection;

using Agw.Setup.Services;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Host.Controllers;

[ApiController]
[Route("api/server-info")]
[Tags("agw-server")]
public sealed class ServerInfoController : ControllerBase
{
    private readonly IInitializationStateStore _stateStore;

    public ServerInfoController(IInitializationStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ServerInfoResponse))]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "0.0.0-dev";
        return AgwApiResult.Ok(new ServerInfoResponse(version, 1, _stateStore.GetSnapshot().IsInitialized));
    }

    public sealed record ServerInfoResponse(string ServerVersion, int ApiMajorVersion, bool Initialized);
}
