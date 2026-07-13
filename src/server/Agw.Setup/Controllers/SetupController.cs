using Agw.Setup.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Configuration;

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

    public SetupController(
        IInitializationStateStore stateStore,
        ISetupInitializationService setupInitializationService,
        SetupCodeService setupCodeService,
        AuthenticationAttemptLimiter attemptLimiter,
        TimeProvider timeProvider)
    {
        _stateStore = stateStore;
        _setupInitializationService = setupInitializationService;
        _setupCodeService = setupCodeService;
        _attemptLimiter = attemptLimiter;
        _timeProvider = timeProvider;
    }

    [HttpGet("")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Index()
    {
        if (_stateStore.GetSnapshot().IsInitialized)
        {
            return NotFound();
        }

        ViewData["RequireSetupCode"] = !LocalTrustedRequest.IsLocalTrusted(HttpContext);
        return View(new SetupRequest
        {
            Provider = DatabaseProvider.Sqlite,
            ConnectionString = "Data Source=agw.db"
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Index(SetupRequest request, CancellationToken cancellationToken)
    {
        ViewData["RequireSetupCode"] = !LocalTrustedRequest.IsLocalTrusted(HttpContext);
        if (_stateStore.GetSnapshot().IsInitialized)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
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
                ModelState.AddModelError(nameof(request.SetupCode), "Too many failed Setup Code attempts. Try again later.");
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
            return Redirect("/");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, $"初始化失败：{ex.Message}");
            return View(request);
        }
    }
}
