using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Agw.Host.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IRepository<Job> _jobRepository;
    private readonly IRepository<Project> _projectRepository;
    private readonly IRepository<ProjectConversation> _projectConversationRepository;
    private readonly IRepository<AgentUsage> _agentUsageRepository;
    private readonly IRepository<ProjectConversationChatHistory> _chatHistoryRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<Agentflow> _agentflowRepository;

    public DashboardController(
        IRepository<Job> jobRepository,
        IRepository<Project> projectRepository,
        IRepository<ProjectConversation> projectConversationRepository,
        IRepository<AgentUsage> agentUsageRepository,
        IRepository<ProjectConversationChatHistory> chatHistoryRepository,
        IRepository<Agent> agentRepository,
        IRepository<Agentflow> agentflowRepository
    )
    {
        _jobRepository = jobRepository;
        _projectRepository = projectRepository;
        _projectConversationRepository = projectConversationRepository;
        _agentUsageRepository = agentUsageRepository;
        _chatHistoryRepository = chatHistoryRepository;
        _agentRepository = agentRepository;
        _agentflowRepository = agentflowRepository;
    }

    [HttpGet("stats")]
    [ProducesApiResult(typeof(DashboardStatsResponse))]
    public async Task<IActionResult> GetStats()
    {
        var stats = new DashboardStatsResponse(
            await _jobRepository.Queryable.CountAsync(),
            await _projectRepository.Queryable.CountAsync(),
            await _projectConversationRepository.Queryable.CountAsync(),
            await _chatHistoryRepository.Queryable.CountAsync(),
            await _agentRepository.Queryable.CountAsync(),
            await _agentflowRepository.Queryable.CountAsync(),
            await _agentUsageRepository.Queryable.SumAsync(usage => usage.InputTokenCount),
            await _agentUsageRepository.Queryable.SumAsync(usage => usage.OutputTokenCount),
            await _agentUsageRepository.Queryable.SumAsync(usage => usage.TotalTokenCount)
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
