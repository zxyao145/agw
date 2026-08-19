using Agw.Shared.Configuration;

namespace Agw.Setup.Contracts;

public sealed class SetupConfiguration
{
    public SetupConfiguration(DeploymentMode deploymentMode, DatabaseProvider provider, string connectionString)
    {
        DeploymentMode = deploymentMode;
        Provider = provider;
        ConnectionString = connectionString;
    }

    public DeploymentMode DeploymentMode { get; }

    public DatabaseProvider Provider { get; }

    public string ConnectionString { get; }
}
