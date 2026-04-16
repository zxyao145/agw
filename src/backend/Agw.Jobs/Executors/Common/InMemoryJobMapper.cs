using Agw.Jobs.Domain.Entities;
using Agw.Jobs.Dtos;

namespace Agw.Jobs.Executors.Common;

internal static class InMemoryJobMapper
{
    public static InMemoryJob FromJob(Job job, long version)
    {
        return new InMemoryJob
        {
            JobId = job.Id,
            ProjectId = job.ProjectId,
            AgentType = job.AgentType,
            AgentId = job.AgentId,
            Name = job.Name,
            Prompt = job.Prompt,
            TriggerType = job.TriggerType,
            TriggerValue = job.TriggerValue,
            NextRunTime = job.NextRunTime,
            RetryCount = job.RetryCount,
            MaxRetryCount = job.MaxRetryCount,
            Version = version
        };
    }

    public static Job ToJob(InMemoryJob inMemoryJob)
    {
        return new Job
        {
            Id = inMemoryJob.JobId,
            ProjectId = inMemoryJob.ProjectId,
            AgentType = inMemoryJob.AgentType,
            AgentId = inMemoryJob.AgentId,
            Name = inMemoryJob.Name,
            Prompt = inMemoryJob.Prompt,
            TriggerType = inMemoryJob.TriggerType,
            TriggerValue = inMemoryJob.TriggerValue,
            NextRunTime = inMemoryJob.NextRunTime,
            RetryCount = inMemoryJob.RetryCount,
            MaxRetryCount = inMemoryJob.MaxRetryCount
        };
    }
}
