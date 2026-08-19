using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Integrations.Application.Capabilities;

internal sealed class ScopedConnectionMcpToolInvoker : IConnectionMcpToolInvoker
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ScopedConnectionMcpToolInvoker(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async ValueTask<object?> InvokeAsync(
        Guid connectionId,
        string sourceId,
        string operationName,
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<IConnectionMcpInvocationSession>();
        return await session.InvokeMcpToolAsync(connectionId, sourceId, operationName, arguments, cancellationToken);
    }
}
