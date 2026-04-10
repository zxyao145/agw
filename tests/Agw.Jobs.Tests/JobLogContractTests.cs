using System.Reflection;

using Agw.Jobs.Domain.Entities;

namespace Agw.Jobs.Tests;

public class JobLogContractTests
{
    [Fact]
    public void JobLog_ExposesJobIdAndTaskIdProperties()
    {
        var propertyNames = typeof(JobLog)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(JobLog.Id), propertyNames);
        Assert.Contains(nameof(JobLog.JobId), propertyNames);
        Assert.Contains("TaskId", propertyNames);
    }
}
