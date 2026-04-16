using Agw.Jobs.Application.Services;
using Agw.Jobs.Contracts;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Repositories;
using Agw.Shared.Domain.Events;

using NSubstitute;

namespace Agw.Jobs.Tests;

public class JobAppServiceTests
{
    [Fact]
    public async Task UpdateAsync_WhenJobExists_AddsJobUpdatedDomainEvent()
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = "before",
            TriggerType = TriggerType.Once,
            TriggerValue = DateTimeOffset.UtcNow.ToString("O"),
            NextRunTime = DateTimeOffset.UtcNow,
            Status = JobStatus.Pending,
            IsEnabled = true
        };

        var jobRepository = Substitute.For<IRepository<Job>>();
        var jobLogRepository = Substitute.For<IRepository<JobLog>>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var jobTimeCalculator = Substitute.For<IJobTimeCalculator>();
        var nextRunTime = DateTimeOffset.UtcNow.AddMinutes(5);

        jobRepository.GetByIdAsync(job.Id).Returns(job);
        jobTimeCalculator.GetNextRunTime(job, Arg.Any<DateTimeOffset>()).Returns(nextRunTime);
        unitOfWork.SaveChangesAsync().Returns(1);

        var service = new JobAppService(jobRepository, jobLogRepository, unitOfWork, jobTimeCalculator);

        var updated = await service.UpdateAsync(job.Id, new JobUpdateRequest
        {
            ProjectId = job.ProjectId,
            Name = "after",
            TriggerType = TriggerType.Once,
            TriggerValue = nextRunTime.ToString("O"),
            MaxRetryCount = 3,
            IsEnabled = true,
            Status = JobStatus.Pending
        }, "tester");

        Assert.Same(job, updated);
        var domainEvent = Assert.Single(job.DomainEvents);
        var updatedEvent = Assert.IsType<JobUpdatedDomainEvent>(domainEvent);
        Assert.Same(job, updatedEvent.Job);
    }
}
