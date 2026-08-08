using Agw.Shared.Contracts.Agents;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Data;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Projects;

namespace Agw.Shared.Tests;

public class TemporalTypePolicyTests
{
    [Fact]
    public void PersistedAndContractTimestamps_UseDateTimeOffset()
    {
        AssertPropertyType<BaseEntity>(nameof(BaseEntity.CreateTime), typeof(DateTimeOffset));
        AssertPropertyType<BaseEntity>(nameof(BaseEntity.UpdateTime), typeof(DateTimeOffset?));
        AssertPropertyType<ProjectConversationChatHistory>(nameof(ProjectConversationChatHistory.CreateTime), typeof(DateTimeOffset));
        AssertPropertyType<ProjectConversationChatHistory>(nameof(ProjectConversationChatHistory.UpdateTime), typeof(DateTimeOffset?));
        AssertPropertyType<ProjectConversationChatHistory>(nameof(ProjectConversationChatHistory.FinishedTime), typeof(DateTimeOffset?));
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
