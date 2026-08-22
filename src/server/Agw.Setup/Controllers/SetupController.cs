using Agw.Auth.Application;
using Agw.Auth.Security;
using Agw.Setup.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Configuration;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Agw.Shared.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Setup.Controllers;

[Route("setup")]
public class SetupController : Controller
{
    private readonly IInitializationStateStore _stateStore;
    private readonly ISetupInitializationService _setupInitializationService;
    private readonly SetupCodeService _setupCodeService;
    private readonly AuthenticationAttemptLimiter _attemptLimiter;
    private readonly TimeProvider _timeProvider;
    private readonly AgwDataPaths _paths;
    private readonly SetupDeploymentOptions _deploymentOptions;

    public SetupController(
        IInitializationStateStore stateStore,
        ISetupInitializationService setupInitializationService,
        SetupCodeService setupCodeService,
        AuthenticationAttemptLimiter attemptLimiter,
        TimeProvider timeProvider,
        AgwDataPaths paths,
        SetupDeploymentOptions? deploymentOptions = null
    )
    {
        _stateStore = stateStore;
        _setupInitializationService = setupInitializationService;
        _setupCodeService = setupCodeService;
        _attemptLimiter = attemptLimiter;
        _timeProvider = timeProvider;
        _paths = paths;
        _deploymentOptions = deploymentOptions ?? new SetupDeploymentOptions();
    }

    [HttpGet("")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesApiResult(StatusCodes.Status404NotFound)]
    public IActionResult Index()
    {
#if !DEBUG
        // 初始化后，setup 页面将返回 404
        if (_stateStore.IsInitialized)
        {
            return ErrorCodes.ResourceNotFound.ToApiResult();
        }
#endif

        ViewData["RequireSetupCode"] = !LocalTrustedRequest.IsLocalTrusted(HttpContext);
        ViewData["RequiredDeploymentMode"] = _deploymentOptions.RequiredMode;
        return View(
            new SetupRequest
            {
                DeploymentMode = _deploymentOptions.RequiredMode ?? DeploymentMode.Standalone,
                Provider =
                    _deploymentOptions.RequiredMode == DeploymentMode.Cluster
                        ? DatabaseProvider.Postgres
                        : DatabaseProvider.Sqlite,
                SqlitePath = _paths.DatabaseFile,
            }
        );
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesApiResult(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Index(SetupRequest request, CancellationToken cancellationToken)
    {
        ViewData["RequireSetupCode"] = !LocalTrustedRequest.IsLocalTrusted(HttpContext);
        ViewData["RequiredDeploymentMode"] = _deploymentOptions.RequiredMode;
        if (_stateStore.IsInitialized)
        {
            return ErrorCodes.ResourceNotFound.ToApiResult();
        }

        if (!ModelState.IsValid)
        {
            return View(request);
        }

        if (_deploymentOptions.RequiredMode is { } requiredMode && request.DeploymentMode != requiredMode)
        {
            ModelState.AddModelError(
                nameof(request.DeploymentMode),
                $"This Host requires the {requiredMode} deployment mode."
            );
            request.DeploymentMode = requiredMode;
            return View(request);
        }

        try
        {
            var requiresSetupCode = !LocalTrustedRequest.IsLocalTrusted(HttpContext);
            var clientKey = AuthenticationAttemptLimiter.GetClientKey(HttpContext);
            var now = _timeProvider.GetUtcNow();
            if (requiresSetupCode && _attemptLimiter.IsBlocked(clientKey, now))
            {
                Response.StatusCode = StatusCodes.Status429TooManyRequests;
                ModelState.AddModelError(
                    nameof(request.SetupCode),
                    "Too many failed Setup Code attempts. Try again later."
                );
                ViewData["RequireSetupCode"] = true;
                return View(request);
            }

            if (requiresSetupCode && !_setupCodeService.Matches(request.SetupCode))
            {
                _attemptLimiter.RecordFailure(clientKey, now);
                ModelState.AddModelError(nameof(request.SetupCode), "Setup Code is invalid or has already been used.");
                ViewData["RequireSetupCode"] = true;
                return View(request);
            }

            await _setupInitializationService.InitializeAsync(request, cancellationToken);
            if (requiresSetupCode)
            {
                _setupCodeService.Consume(request.SetupCode);
            }

            if (request.DeploymentMode == DeploymentMode.Cluster)
            {
                return View("RestartRequired");
            }

            return Redirect("/");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, $"Initialization failed: {ex.Message}");
            return View(request);
        }
    }
}
