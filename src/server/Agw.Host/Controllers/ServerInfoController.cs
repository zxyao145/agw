using System.Reflection;
using Agw.Extensions.Contracts;
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
    private readonly IExtensionDeploymentIdentity _identity;

    public ServerInfoController(IServerInitializationState initializationState, IExtensionDeploymentIdentity identity)
    {
        _initializationState = initializationState;
        _identity = identity;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ServerInfoResponse))]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var version =
            Assembly
                .GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "0.0.0-dev";
        return ApiResult.Ok(
            new ServerInfoResponse
            {
                ServerVersion = version,
                ApiMajorVersion = 1,
                Initialized = _initializationState.IsInitialized,
                ServerId = _initializationState.IsInitialized
                    ? await _identity.GetServerIdAsync(cancellationToken)
                    : null,
            }
        );
    }

    public sealed record ServerInfoResponse
    {
        public required string ServerVersion { get; init; }
        public int ApiMajorVersion { get; init; }
        public bool Initialized { get; init; }
        public string? ServerId { get; init; }
    }
}
