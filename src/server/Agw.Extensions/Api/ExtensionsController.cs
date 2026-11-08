using Agw.Extensions.Application;
using Agw.Extensions.Contracts;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Extensions.Api;

[ApiController]
[Authorize]
[Route("api/extensions")]
public sealed class ExtensionsController : ControllerBase
{
    private readonly ExtensionManagementService _management;
    private readonly ExtensionPackageService _packages;
    private readonly IExtensionDeploymentIdentity _identity;

    public ExtensionsController(
        ExtensionManagementService management,
        ExtensionPackageService packages,
        IExtensionDeploymentIdentity identity
    )
    {
        _management = management;
        _packages = packages;
        _identity = identity;
    }

    [HttpGet]
    [ProducesApiResult(typeof(ExtensionManagementStatus[]))]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        ApiResult.Ok(await _management.ListAsync(cancellationToken));

    [HttpPost("enable")]
    [ProducesApiResult(typeof(ExtensionManagementStatus))]
    public async Task<IActionResult> Enable(ExtensionEnablementRequest request, CancellationToken cancellationToken) =>
        ApiResult.Ok(await _management.SetEnabledAsync(request, true, cancellationToken));

    [HttpPost("disable")]
    [ProducesApiResult(typeof(ExtensionManagementStatus))]
    public async Task<IActionResult> Disable(ExtensionEnablementRequest request, CancellationToken cancellationToken) =>
        ApiResult.Ok(await _management.SetEnabledAsync(request, false, cancellationToken));

    [HttpGet("catalog")]
    [ProducesApiResult(typeof(ExtensionCatalog))]
    public async Task<IActionResult> Catalog([FromQuery] long? afterRevision, CancellationToken cancellationToken)
    {
        var (revision, entries) = await _packages.GetCatalogAsync(afterRevision, cancellationToken);
        return ApiResult.Ok(
            new ExtensionCatalog(await _identity.GetServerIdAsync(cancellationToken), revision, entries)
        );
    }

    [HttpGet("package")]
    [Produces("application/zip")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK)]
    [ProducesApiResult(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(
        [FromQuery] Guid extensionId,
        [FromQuery] string releaseHash,
        CancellationToken cancellationToken
    ) =>
        File(
            await _packages.DownloadAsync(extensionId, releaseHash, cancellationToken),
            "application/zip",
            $"{extensionId:D}-{releaseHash}.zip"
        );
}
