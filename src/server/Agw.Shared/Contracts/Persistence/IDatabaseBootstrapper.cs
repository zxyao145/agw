using Agw.Shared.Configuration;

namespace Agw.Shared.Contracts.Persistence;

public interface IDatabaseBootstrapper
{
    Task InitializeAsync(
        DatabaseProvider provider,
        string connectionString,
        CancellationToken cancellationToken = default
    );
}
