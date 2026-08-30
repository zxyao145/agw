using Agw.Agents.Contracts.Catalog;
using Agw.Jobs.Contracts.Metrics;
using Agw.Projects.Contracts.Metrics;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;

namespace Agw.Host.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IJobMetricsFacade _jobMetrics;
    private readonly IProjectMetricsFacade _projectMetrics;
    private readonly IAgentCatalogFacade _agentCatalog;

    public DashboardController(
        IJobMetricsFacade jobMetrics,
        IProjectMetricsFacade projectMetrics,
        IAgentCatalogFacade agentCatalog
    )
    {
        _jobMetrics = jobMetrics;
        _projectMetrics = projectMetrics;
        _agentCatalog = agentCatalog;
    }

    [HttpGet("stats")]
    [ProducesApiResult(typeof(DashboardStatsResponse))]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var jobs = await _jobMetrics.GetAsync(cancellationToken);
        var projects = await _projectMetrics.GetAsync(cancellationToken);
        var agents = await _agentCatalog.GetMetricsAsync(cancellationToken);
        var stats = new DashboardStatsResponse(
            jobs.JobCount,
            projects.ProjectCount,
            projects.ConversationCount,
            projects.TaskRecordCount,
            agents.AgentCount,
            agents.AgentflowCount,
            projects.InputTokens,
            projects.OutputTokens,
            projects.TotalTokens
        );

        return ApiResult.Ok(stats);
    }
}

public record DashboardStatsResponse(
    int JobCount,
    int ProjectCount,
    int ProjectContextCount,
    int TaskRecordCount,
    int AgentCount,
    int AgentflowCount,
    long UsageInputTokenCount,
    long UsageOutputTokenCount,
    long UsageTotalTokenCount
);
