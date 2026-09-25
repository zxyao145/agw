using System.Security.Claims;
using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Projects.Contracts.History;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Infrastructure.Projects;

/// <summary>
/// 进程内模式的 Turn 只存在于 Host 进程内；Host 启动、接受连接之前，把上次进程残留的活动 Turn 结束为 Interrupted。
/// In-process turns live only inside the Host process; before the Host accepts connections, active turns left by the previous process finish as Interrupted.
/// </summary>
public sealed class InProcessTurnRecovery
{
    private readonly IServiceScopeFactory _scopes;

    public InProcessTurnRecovery(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        await using var scan = _scopes.CreateAsyncScope();
        var database = scan.ServiceProvider.GetRequiredService<AgwDbContext>();
        var candidates = await DbSeeder.ReadInProcessTurnRecoveryCandidatesAsync(database, cancellationToken);
        foreach (var (id, userId) in candidates)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Turn '{id}' cannot be recovered because its conversation has no owner."
                );
            using var owner = UserInfoUtil.Push(
                new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "InProcessTurnRecovery")
                )
            );
            await using var scope = _scopes.CreateAsyncScope();
            await scope
                .ServiceProvider.GetRequiredService<IConversationTurnStore>()
                .FinishAsync(id, ConversationTurnStatus.Interrupted, 0, null, cancellationToken);
        }
    }
}
