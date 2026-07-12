using Agw.Setup.Contracts;

namespace Agw.Setup.Services;

public interface ISetupInitializationService
{
    Task InitializeAsync(SetupRequest request, CancellationToken cancellationToken = default);
}
