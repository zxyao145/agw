using Agw.Setup.Contracts;
using Agw.Setup.Services;

using Microsoft.AspNetCore.Mvc;

namespace Agw.Setup.Controllers;

[Route("setup")]
public class SetupController : Controller
{
    private readonly IInitializationStateStore _stateStore;
    private readonly ISetupInitializationService _setupInitializationService;

    public SetupController(IInitializationStateStore stateStore, ISetupInitializationService setupInitializationService)
    {
        _stateStore = stateStore;
        _setupInitializationService = setupInitializationService;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        if (_stateStore.GetSnapshot().IsInitialized)
        {
            return NotFound();
        }

        return View(new SetupRequest
        {
            Provider = "sqlite",
            ConnectionString = "Data Source=agw.db"
        });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(SetupRequest request, CancellationToken cancellationToken)
    {
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
            await _setupInitializationService.InitializeAsync(request, cancellationToken);
            return Redirect("/");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty, $"初始化失败：{ex.Message}");
            return View(request);
        }
    }
}
