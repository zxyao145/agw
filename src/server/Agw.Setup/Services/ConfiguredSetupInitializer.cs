using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agw.Setup.Services;

public sealed class ConfiguredSetupInitializer
{
    private readonly IInitializationStateStore _stateStore;
    private readonly ISetupInitializationService _setupInitializationService;
    private readonly ConfiguredSetupBootstrap _bootstrap;
    private readonly ILogger<ConfiguredSetupInitializer> _logger;
    private readonly IConfiguration _configuration;

    public ConfiguredSetupInitializer(
        IInitializationStateStore stateStore,
        ISetupInitializationService setupInitializationService,
        ConfiguredSetupBootstrap bootstrap,
        ILogger<ConfiguredSetupInitializer> logger,
        IConfiguration configuration
    )
    {
        _stateStore = stateStore;
        _setupInitializationService = setupInitializationService;
        _bootstrap = bootstrap;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<bool> InitializeIfConfiguredAsync(CancellationToken cancellationToken = default)
    {
        if (_stateStore.IsInitialized)
            return false;

        var bootstrap = _bootstrap.IsConfigured
            ? _bootstrap
            : ConfiguredSetupBootstrap.FromConfiguration(_configuration);
        if (!bootstrap.IsConfigured)
            return false;

        _logger.LogInformation("Initializing Agw administrator from the Setup configuration");
        await _setupInitializationService.InitializeAsync(bootstrap.Request, cancellationToken);
        _logger.LogInformation("Agw initialization from configuration completed");
        return true;
    }
}
