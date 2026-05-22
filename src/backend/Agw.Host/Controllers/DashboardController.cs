using Agw.Agents.Domain.Entities;
using Agw.Jobs.Domain.Entities;
using Agw.Shared.Data.Entities.Tasks;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Results;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Agw.Host.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IRepository<Job> _jobRepository;
    private readonly IRepository<Project> _projectRepository;
    private readonly IRepository<ProjectTask> _projectTaskRepository;
    private readonly IRepository<TaskRecord> _taskRecordRepository;
    private readonly IRepository<Agent> _agentRepository;
    private readonly IRepository<Agentflow> _agentflowRepository;

    public DashboardController(
        IRepository<Job> jobRepository,
        IRepository<Project> projectRepository,
        IRepository<ProjectTask> projectTaskRepository,
        IRepository<TaskRecord> taskRecordRepository,
        IRepository<Agent> agentRepository,
        IRepository<Agentflow> agentflowRepository)
    {
        _jobRepository = jobRepository;
        _projectRepository = projectRepository;
        _projectTaskRepository = projectTaskRepository;
        _taskRecordRepository = taskRecordRepository;
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
            await _projectTaskRepository.Queryable.CountAsync(),
            await _taskRecordRepository.Queryable.CountAsync(),
            await _agentRepository.Queryable.CountAsync(),
            await _agentflowRepository.Queryable.CountAsync());

        return AgwApiResult.Ok(stats);
    }
}

public record DashboardStatsResponse(
    int JobCount,
    int ProjectCount,
    int ProjectTaskCount,
    int ProjectTaskRecordCount,
    int AgentCount,
    int AgentflowCount);
