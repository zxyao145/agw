namespace Agw.Projects.Contracts;

public interface IUserProjectInitializer
{
    Task EnsureDefaultsAsync(CancellationToken cancellationToken = default);
}
