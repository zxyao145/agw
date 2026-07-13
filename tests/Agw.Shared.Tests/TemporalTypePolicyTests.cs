using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Tasks;

namespace Agw.Shared.Tests;

public class TemporalTypePolicyTests
{
    [Fact]
    public void PersistedAndContractTimestamps_UseDateTimeOffset()
    {
        AssertPropertyType<BaseEntity>(nameof(BaseEntity.CreateTime), typeof(DateTimeOffset));
        AssertPropertyType<BaseEntity>(nameof(BaseEntity.UpdateTime), typeof(DateTimeOffset?));
        AssertPropertyType<TaskRecord>(nameof(TaskRecord.CreateTime), typeof(DateTimeOffset));
        AssertPropertyType<TaskRecord>(nameof(TaskRecord.UpdateTime), typeof(DateTimeOffset?));
        AssertPropertyType<TaskRecord>(nameof(TaskRecord.FinishedTime), typeof(DateTimeOffset?));
        AssertPropertyType<AgentflowTrace>(nameof(AgentflowTrace.StartTimeUtc), typeof(DateTimeOffset));
        AssertPropertyType<TaskProjection>(nameof(TaskProjection.CreateTime), typeof(DateTimeOffset));
        AssertPropertyType<TaskProjection>(nameof(TaskProjection.UpdateTime), typeof(DateTimeOffset?));
        AssertPropertyType<TaskProjection>(nameof(TaskProjection.FinishedTime), typeof(DateTimeOffset?));
        AssertPropertyType<AgentflowTraceDto>(nameof(AgentflowTraceDto.StartTimeUtc), typeof(DateTimeOffset));
    }

    private static void AssertPropertyType<T>(string propertyName, Type expectedType)
    {
        var property = typeof(T).GetProperty(propertyName);
        Assert.NotNull(property);
        Assert.Equal(expectedType, property.PropertyType);
    }
}
