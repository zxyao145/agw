using Agw.Shared.Exceptions;
using Agw.Tools.Abstractions.Generated;

namespace Agw.Tools.Generated;

internal sealed class AgwToolInvocationContext : IAgwToolInvocationContextInitializer
{
    private Guid _projectId;
    private bool _initialized;

    public Guid ProjectId =>
        _initialized
            ? _projectId
            : throw new AgwException(ErrorCodes.InvalidParam, "Tool invocation context has not been initialized.");

    public void Initialize(Guid projectId)
    {
        if (_initialized)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Tool invocation context was initialized more than once.");
        }

        _projectId = projectId;
        _initialized = true;
    }
}
