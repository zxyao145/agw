using System.Globalization;
using Agw.Agents.Contracts.Catalog;
using Agw.Jobs.Domain.Repositories;
using Agw.Projects.Contracts.Runtime;
using Agw.Shared.Exceptions;

namespace Agw.Jobs.Domain.Services;

/// <summary>
/// <para>定义任务时需要任务以外事实的规则：目标可见性与默认名称。</para>
/// <para>Rules for defining a job that need facts beyond the job: target visibility and the default name.</para>
/// </summary>
public sealed class JobDefinitionDomainService
{
    private readonly IJobRepository _jobs;
    private readonly IProjectRuntimeFacade _projects;
    private readonly IAgentCatalogFacade _agentCatalog;

    public JobDefinitionDomainService(
        IJobRepository jobs,
        IProjectRuntimeFacade projects,
        IAgentCatalogFacade agentCatalog
    )
    {
        _jobs = jobs;
        _projects = projects;
        _agentCatalog = agentCatalog;
    }

    /// <summary>
    /// <para>任务所在项目必须对所有者可见；同时指定 Agent 类型与 Id 时，该目标必须属于所有者。</para>
    /// <para>The job's project must be visible to the owner; when both agent type and ID are set, that target must belong to the owner.</para>
    /// </summary>
    public async Task EnsureTargetsVisibleAsync(
        Guid projectId,
        AgentRuntimeType? agentType,
        Guid? agentId,
        string ownerUserId
    )
    {
        if (await _projects.GetForCurrentUserAsync(projectId).ConfigureAwait(false) == null)
        {
            throw new AgwException(ErrorCodes.ResourceNotFound);
        }

        if (!agentType.HasValue || !agentId.HasValue)
        {
            return;
        }

        if (!await _agentCatalog.IsOwnedTargetAsync(agentType.Value, agentId.Value, ownerUserId).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.InvalidParam);
        }
    }

    /// <summary>
    /// <para>名称去除两端空白后使用；未命名的任务按所有者已有任务数量生成 job-{序号}-{UTC 日期}。</para>
    /// <para>A name is used trimmed; an unnamed job gets job-{number}-{UTC date} from the owner's current job count.</para>
    /// </summary>
    public async Task<string> ResolveNameAsync(string? requestedName, string ownerUserId, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            return requestedName.Trim();
        }

        var count = await _jobs.CountByOwnerAsync(ownerUserId).ConfigureAwait(false);
        return $"job-{count + 1}-{now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
    }
}
