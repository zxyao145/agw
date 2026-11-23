using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.ValueObjects;
using Agw.Agents.Execution.Agentflows.Checkpoints;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Agents.Tests;

public sealed partial class AgentflowAppServiceTests
{
    [Theory]
    [InlineData("active-manifest-root")]
    [InlineData("active-manifest-nested-target")]
    [InlineData("resumable-checkpoint-nested-target")]
    public async Task DeleteAgentflow_ReferencedByActiveManifestOrResumableCheckpoint_IsRejected(string usage)
    {
        var userInfo = new TestUserInfoService();
        await using var connection = await OpenConnectionAsync();
        await using var dbContext = await CreateDbContextAsync(connection);
        var agent = await SeedAgentAsync(dbContext, "Runner", "tester");
        var service = CreateService(dbContext, userInfo);
        var target = await service.CreateAsync("target-flow", null, CreateDefinition(agent.Id));
        await service.PublishAsync(target.Id, 1);
        var includesNestedTarget = usage != "active-manifest-root";
        var root = includesNestedTarget
            ? await service.CreateAsync("root-flow", null, CreateNestedDefinition(target.Id))
            : target;
        if (includesNestedTarget)
            await service.PublishAsync(root.Id, 1);
        var isResumableCheckpoint = usage == "resumable-checkpoint-nested-target";
        var turnId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        var dependencies = includesNestedTarget
            ? new[] { new AgentflowDependencyRevision(root.Id, 1, "nested", null, target.Id, 1) }
            : Array.Empty<AgentflowDependencyRevision>();
        var snapshot = new AgentflowRevisionSnapshot(1, dependencies);

        dbContext.Projects.Add(
            new Project
            {
                Id = projectId,
                Name = "Pinned flow",
                CreateBy = "tester",
            }
        );
        dbContext.ProjectConversations.Add(
            new ProjectConversation
            {
                Id = conversationId,
                ProjectId = projectId,
                ContextId = "pinned-context",
                Title = "Pinned flow",
                CreateBy = "tester",
            }
        );
        if (isResumableCheckpoint)
        {
            dbContext.ProjectConversationTurns.Add(
                new ProjectConversationTurn
                {
                    Id = turnId,
                    ProjectConversationId = conversationId,
                    TaskId = Guid.CreateVersion7(),
                    TargetId = root.Id,
                    RuntimeType = AgentRuntimeType.Agentflow,
                    Status = ProjectConversationTurnStatus.Accepted,
                    InputMessageId = Guid.CreateVersion7(),
                    FirstSequence = 0,
                    StartedAt = UtcNow,
                    Extra = new ProjectConversationTurnExtra
                    {
                        AgentflowRevision = snapshot.RootRevision,
                        AgentflowDependencies = snapshot
                            .Dependencies.Select(dependency => new ProjectConversationTurnAgentflowDependency(
                                dependency.ParentAgentflowId,
                                dependency.ParentRevision,
                                dependency.ParentNodeId,
                                dependency.BlockMemberId,
                                dependency.TargetAgentflowId,
                                dependency.TargetRevision
                            ))
                            .ToArray(),
                    },
                }
            );
            dbContext.AgentflowCheckpoints.Add(
                new AgentflowCheckpointRecord
                {
                    Id = Guid.CreateVersion7(),
                    SourceTurnId = turnId,
                    ProjectId = projectId,
                    ProjectConversationId = conversationId,
                    ContextId = "pinned-context",
                    TaskId = Guid.CreateVersion7(),
                    AgentflowId = root.Id,
                    AgentflowRevision = snapshot.RootRevision,
                    RuntimeVersion = AgentflowCheckpointRuntime.Version,
                    UserId = "tester",
                    IsDurable = true,
                    BoundarySequence = 1,
                    MarkersJson = "[]",
                    CheckpointJson = "{}",
                    CreateBy = "tester",
                    CreateTime = UtcNow,
                }
            );
            dbContext.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = turnId,
                    UserId = "tester",
                    ProjectId = projectId,
                    ProjectConversationId = conversationId,
                    ScopeBackfilled = true,
                    ManifestJson = DurableExecutionJson.Serialize(
                        new DurableExecutionManifest
                        {
                            TurnId = turnId,
                            UserId = "tester",
                            AgentId = root.Id,
                            AgentType = AgentRuntimeType.Agentflow,
                            AgentflowRevision = snapshot.RootRevision,
                            AgentflowDependencies = snapshot.Dependencies,
                            Input = new AgwUserInput
                            {
                                Author = "user",
                                Contents = [new AgwTextContent { Content = "input" }],
                            },
                            Task = new DurableProjectTaskSnapshot
                            {
                                TaskId = Guid.CreateVersion7(),
                                ProjectId = projectId,
                                ProjectConversationId = conversationId,
                                ContextId = "pinned-context",
                            },
                            Settings = new DurableExecutionSettings { EnvironmentVariables = [], Resume = false },
                        }
                    ),
                    Status = DurableExecutionStatus.Completed,
                    StateChangedAt = UtcNow,
                    StateVersion = Guid.CreateVersion7(),
                    CreateBy = "tester",
                    CreateTime = UtcNow,
                }
            );
        }
        else
        {
            dbContext.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = turnId,
                    UserId = "tester",
                    ProjectId = projectId,
                    ProjectConversationId = conversationId,
                    ScopeBackfilled = true,
                    ManifestJson = DurableExecutionJson.Serialize(
                        new DurableExecutionManifest
                        {
                            TurnId = turnId,
                            UserId = "tester",
                            AgentId = root.Id,
                            AgentType = AgentRuntimeType.Agentflow,
                            AgentflowRevision = snapshot.RootRevision,
                            AgentflowDependencies = snapshot.Dependencies,
                            Input = new AgwUserInput
                            {
                                Author = "user",
                                Contents = [new AgwTextContent { Content = "input" }],
                            },
                            Task = new DurableProjectTaskSnapshot
                            {
                                TaskId = Guid.CreateVersion7(),
                                ProjectId = projectId,
                                ProjectConversationId = conversationId,
                                ContextId = "pinned-context",
                            },
                            Settings = new DurableExecutionSettings { EnvironmentVariables = [], Resume = false },
                        }
                    ),
                    Status = DurableExecutionStatus.Running,
                    StateChangedAt = UtcNow,
                    StateVersion = Guid.CreateVersion7(),
                    CreateBy = "tester",
                    CreateTime = UtcNow,
                }
            );
        }
        await dbContext.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<AgwException>(() => service.DeleteAsync(target.Id));

        Assert.Equal(ErrorCodes.AgentflowInUse.Code, exception.Code);
        Assert.True(await dbContext.Agentflows.AnyAsync(flow => flow.Id == target.Id));

        if (isResumableCheckpoint)
        {
            var checkpoint = await dbContext.AgentflowCheckpoints.SingleAsync();
            dbContext.AgentflowCheckpoints.Remove(checkpoint);
        }
        else
        {
            var execution = await dbContext.DurableExecutions.SingleAsync();
            execution.Status = DurableExecutionStatus.Completed;
        }
        await dbContext.SaveChangesAsync();

        if (root.Id != target.Id)
            Assert.True(await service.DeleteAsync(root.Id));
        Assert.True(await service.DeleteAsync(target.Id));
        Assert.False(await dbContext.Agentflows.AnyAsync(flow => flow.Id == target.Id));
        Assert.False(await dbContext.AgentflowRevisions.AnyAsync(item => item.AgentflowId == target.Id));
    }

    private static AgentflowDefinition CreateNestedDefinition(Guid targetAgentflowId) =>
        new(
            [
                new AgentflowNode
                {
                    NodeId = "input",
                    Kind = AgentflowNodeKind.Input,
                    Name = "Input",
                    RoutingStrategy = AgentflowRoutingStrategy.FanOut,
                    ConfigJson = AgentflowDefinitionJson.EmptyOptions,
                },
                new AgentflowNode
                {
                    NodeId = "nested",
                    Kind = AgentflowNodeKind.WorkflowAsAgent,
                    Name = "Nested workflow",
                    RelateId = targetAgentflowId,
                    RoutingStrategy = AgentflowRoutingStrategy.Direct,
                    ConfigJson = AgentflowDefinitionJson.EmptyOptions,
                },
                new AgentflowNode
                {
                    NodeId = "output",
                    Kind = AgentflowNodeKind.Output,
                    Name = "Output",
                    RoutingStrategy = AgentflowRoutingStrategy.Direct,
                    ConfigJson = AgentflowDefinitionJson.WriteOptions(new OutputOptions(false)),
                },
            ],
            [CreateEdge("input-nested", "input", "nested"), CreateEdge("nested-output", "nested", "output")],
            []
        );
}
