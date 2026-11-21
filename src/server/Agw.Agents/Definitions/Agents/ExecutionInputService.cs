using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Contracts;
using Agw.Auth.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Agw.Agents.Definitions.Agents;

/// <summary>
/// 查询执行目标在 Turn 运行期间接收输入的能力；目标必须属于当前用户。
/// Queries whether an execution target accepts input while a turn runs; the target must belong to the current user.
/// </summary>
public sealed class ExecutionInputService
{
    private readonly IAgentsDbContext _db;
    private readonly ExecutionProvider _provider;

    public ExecutionInputService(IAgentsDbContext db, IConfiguration configuration)
    {
        _db = db;
        _provider = Enum.TryParse<ExecutionProvider>(
            configuration[ExecutionRuntimeConfiguration.ProviderKey],
            ignoreCase: true,
            out var provider
        )
            ? provider
            : ExecutionProvider.InProcess;
    }

    public async Task<ExecutionInputCapabilities> GetAsync(
        AgentRuntimeType type,
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var owner = UserInfoUtil.RequiredUserId;
        if (type == AgentRuntimeType.Agent)
        {
            var agent =
                await _db
                    .Agents.AsNoTracking()
                    .SingleOrDefaultAsync(
                        candidate => candidate.Id == id && candidate.CreateBy == owner,
                        cancellationToken
                    )
                ?? throw new AgwException(ErrorCodes.ResourceNotFound);
            return ExecutionInputCapabilities.ForEngine(
                agent.Type == AgentType.System ? EngineKind.Maf : agent.ExternalAgentKind,
                _provider
            );
        }

        if (type != AgentRuntimeType.Agentflow)
            throw new AgwException(ErrorCodes.InvalidParam);
        if (
            !await _db
                .Agentflows.AsNoTracking()
                .AnyAsync(candidate => candidate.Id == id && candidate.CreateBy == owner, cancellationToken)
        )
            throw new AgwException(ErrorCodes.ResourceNotFound);
        return ExecutionInputCapabilities.ForAgentflow();
    }
}
