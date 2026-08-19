using Microsoft.Extensions.Logging;

namespace Agw.Setup.Services;

public sealed class ConfiguredSetupInitializer
{
    private readonly IInitializationStateStore _stateStore;
    private readonly ISetupInitializationService _setupInitializationService;
    private readonly ConfiguredSetupBootstrap _bootstrap;
    private readonly ILogger<ConfiguredSetupInitializer> _logger;

    public ConfiguredSetupInitializer(
        IInitializationStateStore stateStore,
        ISetupInitializationService setupInitializationService,
        ConfiguredSetupBootstrap bootstrap,
        ILogger<ConfiguredSetupInitializer> logger
    )
    {
        _stateStore = stateStore;
        _setupInitializationService = setupInitializationService;
        _bootstrap = bootstrap;
        _logger = logger;
    }

    public async Task<bool> InitializeIfConfiguredAsync(CancellationToken cancellationToken = default)
    {
        if (_stateStore.IsInitialized || !_bootstrap.IsConfigured)
            return false;

        _logger.LogInformation(
            "Initializing Agw from the Setup configuration using {DeploymentMode} deployment and {DatabaseProvider}",
            _bootstrap.Request.DeploymentMode,
            _bootstrap.Request.Provider
        );
        await _setupInitializationService.InitializeAsync(_bootstrap.Request, cancellationToken);
        _logger.LogInformation("Agw initialization from configuration completed");
        return true;
    }
}
