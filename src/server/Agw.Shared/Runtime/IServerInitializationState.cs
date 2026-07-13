using Agw.Shared.Configuration;

namespace Agw.Shared.Runtime;

public interface IServerInitializationState
{
    bool IsInitialized { get; }
    DatabaseProvider DatabaseProvider { get; }
    string DatabaseConnectionString { get; }
}
