using System.Reflection;

using Agw.Jobs.Contracts;
using Agw.Shared.Data.Entities.Jobs;

namespace Agw.Jobs.Tests;

public class JobLogContractTests
{
    [Fact]
    public void JobLog_KeepsInternalTaskIdProperty()
    {
        var propertyNames = typeof(JobLog)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(JobLog.Id), propertyNames);
        Assert.Contains(nameof(JobLog.JobId), propertyNames);
        Assert.Contains("TaskId", propertyNames);
    }

    [Fact]
    public void JobLogResponse_DoesNotExposeTaskId()
    {
        var propertyNames = typeof(JobLogResponse)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(JobLogResponse.ContextId), propertyNames);
        Assert.DoesNotContain("TaskId", propertyNames);
    }
}
