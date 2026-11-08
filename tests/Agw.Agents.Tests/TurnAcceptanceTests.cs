using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Facades;
using Agw.Agents.Definitions.Persistence;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Agents.Execution.Inbound.Connections;
using Agw.Agents.Execution.Persistence.Durable;
using Agw.Agents.Execution.Runtimes.Durable;
using Agw.Agents.Execution.Turns;
using Agw.Auth.Application;
using Agw.Extensions;
using Agw.Extensions.Application.Packages;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Files;
using Agw.Files.Abstracts;
using Agw.Infrastructure;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Infrastructure.Settings;
using Agw.Integrations.Application.Facades;
using Agw.Projects;
using Agw.Projects.Application;
using Agw.Projects.Application.Facades;
using Agw.Projects.Application.History;
using Agw.Projects.Contracts.Runtime;
using Agw.Projects.Domain.Services;
using Agw.Providers.Application.Facades;
using Agw.Settings;
using Agw.Settings.Application.Persistence;
using Agw.Shared.Contracts;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Tooling;
using Agw.Skills.Application.Facades;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

namespace Agw.Agents.Tests;

/// <summary>
/// 受理事务：输入行、Turn 行与 Durable 记录在一个事务中提交，turnId 幂等且只对所属用户与对话可见。
/// The acceptance transaction: the input row, turn row and durable record commit in one transaction, and turnIds are idempotent and visible only to their owner and conversation.
/// </summary>
public sealed class TurnAcceptanceTests : IAsyncLifetime
{
    private const string UserId = TurnPersistenceTestKit.UserId;

    private readonly IDisposable _user = TurnPersistenceTestKit.EnterUser();
    private TurnPersistenceTestKit _kit = null!;

    public async ValueTask InitializeAsync() => _kit = await TurnPersistenceTestKit.CreateAsync();

    public async ValueTask DisposeAsync()
    {
        _user.Dispose();
        await _kit.DisposeAsync();
    }

    [Fact]
    public async Task AcceptAsync_InProcess_WritesInputRowAndTurnBeforeStart()
    {
        // Arrange
        var task = await _kit.SeedConversationAsync();
        var input = TurnPersistenceTestKit.CreateInput("hello");

        // Act
        var accepted = await AcceptAsync(task, input: input);

        // Assert
        Assert.True(accepted.Created);
        Assert.NotNull(accepted.Broadcast);
        Assert.True(AgwMessageClassifier.IsTurnStart(accepted.Start));
        Assert.Equal(1L, accepted.Start.AdditionalProperties?[TurnMessageFactory.TurnSequenceKey]);
        var turnId = accepted.Request.TurnId;
        Assert.Equal(turnId.ToString("D"), accepted.Start.AdditionalProperties?["streamingScopeId"]);
        var inputId = Guid.Parse(input.MessageId);
        var turn = await _kit.ReadTurnAsync(turnId);
        Assert.Equal(ProjectConversationTurnStatus.Accepted, turn.Status);
        Assert.Equal(task.ProjectConversationId, turn.ProjectConversationId);
        Assert.Equal(inputId, turn.InputMessageId);
        Assert.Equal(0, turn.FirstSequence);
        var row = Assert.Single(await _kit.ReadHistoryAsync(task.ProjectConversationId));
        Assert.Equal(inputId, row.Id);
        Assert.Equal(turnId, row.TurnId);
        Assert.Equal(0, row.StepIndex);
        Assert.Equal(ConversationMessagePurpose.Input, row.Purpose);
        Assert.Null(row.HistoryScope);
        Assert.Equal(0, row.Metadata![ConversationHandoffMetadata.ThroughSequenceKey].GetInt64());
        Assert.Equal(turnId, row.Metadata["producerScopeId"].GetGuid());
        Assert.Equal("hello", row.GetText());
        await using var context = _kit.CreateContext();
        Assert.False(
            await context.DurableExecutions.IgnoreQueryFilters().AnyAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task AcceptAsync_Durable_CommitsRecordWithStartEvent()
    {
        // Arrange
        var task = await _kit.SeedConversationAsync();

        // Act
        var accepted = await AcceptAsync(task, durable: _kit.Coordinator);

        // Assert
        var turnId = accepted.Request.TurnId;
        Assert.Null(accepted.Request.Lease);
        var record = await _kit.ReadExecutionAsync(turnId);
        Assert.Equal(DurableExecutionStatus.Queued, record.Status);
        Assert.Equal(0, record.LeaseEpoch);
        Assert.Null(record.WorkerId);
        Assert.Equal(1, record.LastEventSequence);
        var start = DurableExecutionEvents.ToEntry(Assert.Single(await _kit.ReadEventsAsync(turnId)));
        Assert.Equal(1, start.Sequence);
        Assert.True(AgwMessageClassifier.IsTurnStart(start.Message));
        Assert.Equal(
            turnId.ToString("D"),
            start.Message.AdditionalProperties?[TurnMessageFactory.TurnIdKey]?.ToString()
        );
        Assert.Equal(ProjectConversationTurnStatus.Accepted, (await _kit.ReadTurnAsync(turnId)).Status);
    }

    [Fact]
    public async Task AcceptAsync_TurnIdOfAnotherUserOrConversation_FailsLikeMissingConversation()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var owned = await _kit.SeedConversationAsync();
        var otherConversation = await _kit.SeedConversationAsync(contextId: "context-2");
        var turnId = (await AcceptAsync(owned)).Request.TurnId;
        AgentExecutionTask foreignTask;
        using (TurnPersistenceTestKit.EnterUser("another-user"))
            foreignTask = await _kit.SeedConversationAsync("another-user");

        // Act
        var otherConversationError = await Assert.ThrowsAsync<AgwException>(() =>
            AcceptAsync(otherConversation, turnId)
        );
        AgwException foreignUserError;
        using (TurnPersistenceTestKit.EnterUser("another-user"))
            foreignUserError = await Assert.ThrowsAsync<AgwException>(() =>
                AcceptAsync(foreignTask, turnId, userId: "another-user")
            );

        // Assert
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, otherConversationError.Code);
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, foreignUserError.Code);
        Assert.Empty(await _kit.ReadHistoryAsync(otherConversation.ProjectConversationId));
        Assert.Empty(await _kit.ReadHistoryAsync(foreignTask.ProjectConversationId));
        Assert.Equal(owned.ProjectConversationId, (await _kit.ReadTurnAsync(turnId)).ProjectConversationId);
        await using var context = _kit.CreateContext();
        Assert.Equal(1, await context.ProjectConversationTurns.IgnoreQueryFilters().CountAsync(token));
    }

    [Fact]
    public async Task AcceptAsync_DurableRegistrationFails_RollsBackInputAndTurn()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var task = await _kit.SeedConversationAsync();
        var turnId = Guid.CreateVersion7();
        await using (var context = _kit.CreateContext())
        {
            // 同一 ID 的执行记录已经存在且没有 Turn 行：登记时主键冲突，整个受理事务回滚。
            // An execution record with the same ID exists without a turn row: registration hits a key conflict and the whole acceptance rolls back.
            context.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = turnId,
                    UserId = "another-user",
                    ManifestJson = "{}",
                    StateChangedAt = DateTimeOffset.UtcNow,
                    StateVersion = Guid.CreateVersion7(),
                }
            );
            await context.SaveChangesAsync(token);
        }

        // Act
        var error = await Assert.ThrowsAsync<AgwException>(() => AcceptAsync(task, turnId, durable: _kit.Coordinator));

        // Assert
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, error.Code);
        Assert.Empty(await _kit.ReadHistoryAsync(task.ProjectConversationId));
        await using var assertContext = _kit.CreateContext();
        Assert.False(await assertContext.ProjectConversationTurns.IgnoreQueryFilters().AnyAsync(token));
        Assert.False(
            await assertContext
                .DurableExecutionEvents.IgnoreQueryFilters()
                .AnyAsync(entry => entry.TurnId == turnId, token)
        );
    }

    [Fact]
    public async Task ReportStartFailureAsync_InProcess_WritesErrorAndFailedFinishAfterStart()
    {
        // Arrange
        var task = await _kit.SeedConversationAsync();
        var acceptance = CreateAcceptance();
        var accepted = await AcceptAsync(task, acceptance: acceptance);
        var sink = new InteractionTestSink();
        accepted.Broadcast!.AddSink(sink);
        await accepted.Broadcast.WriteAsync(accepted.Start, TestContext.Current.CancellationToken);

        // Act
        await acceptance.ReportStartFailureAsync(
            accepted,
            new AgwException(ErrorCodes.AgentExecutionFailed, "start failed")
        );

        // Assert
        Assert.Equal(
            [1L, 2L, 3L],
            sink.Messages.Select(message => message.AdditionalProperties?[TurnMessageFactory.TurnSequenceKey])
        );
        Assert.True(AgwMessageClassifier.IsTurnStart(sink.Messages[0]));
        Assert.Equal("start failed", Assert.IsType<AgwErrorContent>(Assert.Single(sink.Messages[1].Contents)).Content);
        Assert.True(AgwMessageClassifier.TryGetTurnFinishedStatus(sink.Messages[2], out var status));
        Assert.Equal(AgwTurnStatus.Failed, status);
        var errorCode = ErrorCodes.AgentExecutionFailed.Code.ToString(CultureInfo.InvariantCulture);
        Assert.Equal(errorCode, sink.Messages[2].AdditionalProperties?["errorCode"]);
        var turn = await _kit.ReadTurnAsync(accepted.Request.TurnId);
        Assert.Equal(ProjectConversationTurnStatus.Failed, turn.Status);
        Assert.Equal(errorCode, turn.ErrorCode);
        Assert.Equal(0, turn.LastSequence);
    }

    [Fact]
    public async Task AcceptAsync_ExtensionFirstTurnRejected_KeepsCreatorAndAllowsRetry()
    {
        // Arrange: the extension's first turn names a missing Agent, so acceptance fails after the task resolves.
        // 准备：extension 的第一个 Turn 指向不存在的 Agent，受理在解析任务之后失败。
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await _kit.SeedProjectAsync(projectId);
        var acceptance = CreateResolvingAcceptance();
        var rejected = await Assert.ThrowsAsync<AgwException>(() =>
            AcceptForAsync(acceptance, projectId, conversationId, Guid.CreateVersion7(), TestExtensionIds.Sample)
        );
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, rejected.Code);
        var agentId = await SeedAgentAsync();

        // Act
        var other = await Assert.ThrowsAsync<AgwException>(() =>
            AcceptForAsync(acceptance, projectId, conversationId, agentId, TestExtensionIds.Other)
        );
        var retried = await AcceptForAsync(acceptance, projectId, conversationId, agentId, TestExtensionIds.Sample);

        // Assert
        Assert.Equal(ErrorCodes.ConversationReadOnly.Code, other.Code);
        Assert.True(retried.Created);
        Assert.Equal(TestExtensionIds.Sample, (await _kit.ReadConversationAsync(conversationId)).ExtensionId);
        Assert.Equal(1, await _kit.CountTurnsAsync(conversationId));
    }

    [Fact]
    public async Task AcceptAsync_HostFirstTurnRejected_LeavesOpenConversationForExtension()
    {
        // Arrange: the host's first turn fails after the task resolves, leaving a host conversation without turns.
        // 准备：宿主的第一个 Turn 在解析任务之后失败，留下没有 Turn 的宿主对话。
        var projectId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await _kit.SeedProjectAsync(projectId);
        var acceptance = CreateResolvingAcceptance();
        var rejected = await Assert.ThrowsAsync<AgwException>(() =>
            AcceptForAsync(acceptance, projectId, conversationId, Guid.CreateVersion7(), extensionId: null)
        );
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, rejected.Code);
        Assert.Equal(
            Agw.Shared.Contracts.Conversations.ConversationCreators.Host,
            (await _kit.ReadConversationAsync(conversationId)).ExtensionId
        );
        var agentId = await SeedAgentAsync();

        // Act
        var accepted = await AcceptForAsync(acceptance, projectId, conversationId, agentId, TestExtensionIds.Sample);

        // Assert
        Assert.True(accepted.Created);
        Assert.Equal(TestExtensionIds.Sample, (await _kit.ReadConversationAsync(conversationId)).ExtensionId);
        Assert.Equal(1, await _kit.CountTurnsAsync(conversationId));
    }

    [Fact]
    public Task AcceptAsync_EnabledCompiledSkillProvider_PersistsExtensionIdWithoutSkillRows() =>
        AssertCompiledExtensionCapabilitiesAsync(backgroundOnly: false, durable: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task AcceptAsync_BackgroundSystemAgentTool_IsAvailableInAcceptedCapabilities(bool durable) =>
        AssertCompiledExtensionCapabilitiesAsync(backgroundOnly: true, durable);

    private static async Task AssertCompiledExtensionCapabilitiesAsync(bool backgroundOnly, bool durable)
    {
        var token = TestContext.Current.CancellationToken;
        var extensionId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a100");
        var extensionRoot = Path.Combine(
            AppContext.BaseDirectory,
            ".scratch",
            "turn-extension-acceptance",
            Guid.CreateVersion7().ToString("N")
        );
        var extensionDirectory = Path.Combine(extensionRoot, "extensions");
        Directory.CreateDirectory(extensionDirectory);
        var runtimeDataPaths = AgwDataPaths.Resolve(
            Path.Combine(extensionRoot, "data"),
            extensionRoot,
            Path.Combine(extensionRoot, "logs")
        );

        try
        {
            await CreateCompiledExtensionPackageAsync(extensionDirectory, extensionId, token);
            var extensionConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Extensions:Directory"] = extensionDirectory }
                )
                .Build();
            await using var kit = await TurnPersistenceTestKit.CreateAsync(configure: services =>
            {
                services.AddSingleton(runtimeDataPaths);
                services.AddSingleton<IHostEnvironment>(
                    new HostingEnvironment
                    {
                        ApplicationName = typeof(TurnAcceptanceTests).Assembly.GetName().Name!,
                        EnvironmentName = "Testing",
                        ContentRootPath = AppContext.BaseDirectory,
                        ContentRootFileProvider = new PhysicalFileProvider(AppContext.BaseDirectory),
                    }
                );
                services.AddScoped<ISettingsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
                services.AddScoped<ISettingsPersistence, EfSettingsPersistence>();
                services.AddScoped<ICurrentUser, UserInfoService>();
                services.AddScoped<IUserInfoService, UserInfoService>();
                services.AddSettings();
                services.AddExtensionInfrastructure();
                services.AddExtensions(extensionConfiguration, "Standalone");
                services.AddExtensionCapabilities();
            });
            await using IExtensionRuntime extensionRuntime = kit.Services.GetRequiredService<IExtensionRuntime>();

            ExtensionPackageCatalog packages = kit.Services.GetRequiredService<ExtensionPackageCatalog>();
            await packages.InitializeAsync(token);
            ExtensionLocalPackage package = packages.GetValidPackage(extensionId);
            IExtensionEnablementStore enablement = kit.Services.GetRequiredService<IExtensionEnablementStore>();
            ExtensionEnablementState enabled = await enablement.SetAsync(extensionId, true, token);
            ExtensionServerManifest server = package.Manifest.Server!;
            var loadRequest = new ExtensionRuntimeLoadRequest
            {
                ExtensionId = extensionId,
                Name = package.Manifest.Name,
                Version = package.Manifest.Version,
                ReleaseHash = package.ReleaseHash,
                Epoch = enabled.Version,
                DataDirectory = Path.Combine(runtimeDataPaths.Root, "extensions", "data", extensionId.ToString("N")),
                PackageDirectory = package.SnapshotDirectory,
                EntryAssemblyPath = server.EntryAssembly,
                EntryType = server.EntryType,
            };
            ExtensionGenerationSnapshot generation = await extensionRuntime.LoadAsync(loadRequest, token);
            await extensionRuntime.PrepareDatabaseAsync(generation.GenerationId, token);
            await extensionRuntime.ActivateAsync(generation.GenerationId, token);
            await extensionRuntime.ResumeAsync(generation.GenerationId, token);

            var task = await kit.SeedConversationAsync();
            await using var context = kit.CreateContext();
            var currentUser = new UserInfoService();
            var agentId = Guid.CreateVersion7();
            var backgroundAgentId = Guid.CreateVersion7();
            List<Guid> backgroundAgentIds = [agentId, backgroundAgentId, backgroundAgentId];
            ToolValue extensionTool = new()
            {
                Definition = new ExtensionToolDefinition { ExtensionId = extensionId, ToolId = "calendar-lookup" },
            };
            if (backgroundOnly)
            {
                var nestedAgentId = Guid.CreateVersion7();
                var externalAgentId = Guid.CreateVersion7();
                var foreignAgentId = Guid.CreateVersion7();
                backgroundAgentIds.AddRange([externalAgentId, foreignAgentId, Guid.CreateVersion7()]);
                ToolBlockValue nestedBlock = new()
                {
                    Definition = new BackgroundAgentsToolBlockDefinition
                    {
                        Options = new BackgroundAgentsToolBlockOptions { AllowedAgentIds = [nestedAgentId] },
                    },
                };
                ToolValue unavailableTool = new()
                {
                    Definition = new ExtensionToolDefinition
                    {
                        ExtensionId = Guid.CreateVersion7(),
                        ToolId = "unavailable",
                    },
                };
                context.Agents.AddRange(
                    new Agent
                    {
                        Id = backgroundAgentId,
                        Name = "background-system",
                        Type = AgentType.System,
                        Tools = [extensionTool, nestedBlock],
                        CreateBy = UserId,
                    },
                    new Agent
                    {
                        Id = nestedAgentId,
                        Name = "nested-background",
                        Type = AgentType.System,
                        Tools = [unavailableTool],
                        CreateBy = UserId,
                    },
                    new Agent
                    {
                        Id = externalAgentId,
                        Name = "background-external",
                        Type = AgentType.External,
                        Tools = [unavailableTool],
                        CreateBy = UserId,
                    },
                    new Agent
                    {
                        Id = foreignAgentId,
                        Name = "foreign-background",
                        Type = AgentType.System,
                        Tools = [unavailableTool],
                        CreateBy = "another-user",
                    }
                );
            }
            context.Agents.Add(
                new Agent
                {
                    Id = agentId,
                    Name = "extension-skill-provider-agent",
                    Type = AgentType.System,
                    Tools = backgroundOnly
                        ?
                        [
                            new ToolBlockValue
                            {
                                Definition = new BackgroundAgentsToolBlockDefinition
                                {
                                    Options = new BackgroundAgentsToolBlockOptions
                                    {
                                        AllowedAgentIds = backgroundAgentIds,
                                    },
                                },
                            },
                        ]
                        : [extensionTool],
                    CreateBy = UserId,
                }
            );
            await context.SaveChangesAsync(token);

            var projectServices = new ServiceCollection();
            projectServices.AddLogging();
            projectServices.AddSingleton(runtimeDataPaths);
            projectServices.AddFiles(extensionConfiguration);
            projectServices.AddProjects(extensionConfiguration);
            using var projectProvider = projectServices.BuildServiceProvider();
            IProjectWorkspaceAccess workspaceAccess = projectProvider.GetRequiredService<IProjectWorkspaceAccess>();
            ProjectOwnershipCache ownershipCache = projectProvider.GetRequiredService<ProjectOwnershipCache>();
            await using var maintenanceScope = kit.Services.CreateAsyncScope();
            var projectService = new ProjectAppService(
                context,
                new ProjectResourceBindingDomainService(
                    new AgentCatalogFacade(context, currentUser),
                    new SkillReferenceFacade(context, currentUser),
                    new ConnectionReferenceFacade(context, currentUser)
                ),
                new ProjectDeletionCoordinator(
                    context,
                    kit.Locks,
                    maintenanceScope.ServiceProvider.GetRequiredService<IDurableExecutionScopeMaintenance>(),
                    kit.Clock
                ),
                new ProjectResolver(context, currentUser),
                currentUser,
                runtimeDataPaths,
                workspaceAccess,
                ownershipCache
            );
            var projectRuntime = new ProjectRuntimeFacade(projectService, workspaceAccess);
            var skillReferences = new SkillReferenceFacade(context, currentUser);
            var connectionReferences = new ConnectionReferenceFacade(context, currentUser);
            var modelProviderReferences = new ModelProviderReferenceFacade(context, currentUser);
            var definitionRepository = new AgentDefinitionRepository(context, currentUser);
            var agents = new AgentAppService(
                context,
                new AgentDefinitionDomainService(definitionRepository, modelProviderReferences),
                new AgentResourceBindingDomainService(definitionRepository, skillReferences, connectionReferences),
                modelProviderReferences,
                skillReferences,
                currentUser,
                new AgentDeletionCoordinator(context, kit.Locks),
                kit.Locks
            );
            IExtensionCapabilityCatalog capabilityCatalog =
                kit.Services.GetRequiredService<IExtensionCapabilityCatalog>();
            var selectionResolver = new ExtensionExecutionSelectionResolver(
                agents,
                new AgentflowDefinitionReader(context),
                capabilityCatalog
            );
            ProjectRuntimeSnapshot project = Assert.IsType<ProjectRuntimeSnapshot>(
                await projectRuntime.GetForCurrentUserAsync(task.ProjectId, token)
            );
            var selection = await selectionResolver.ResolveAsync(
                new ExecutionTarget(agentId, AgentRuntimeType.Agent),
                project,
                token
            );
            ExtensionCapabilitySnapshot? cachedCapabilities = null;

            Assert.Equal([new ExtensionToolSelection(extensionId, "calendar-lookup")], selection.Tools);
            Assert.Equal([extensionId], selection.SkillExtensionIds);

            var acceptance = kit.CreateAcceptance(
                projectTasks: null,
                projectRuntime,
                durable: durable ? kit.Coordinator : null,
                extensionSelectionResolver: selectionResolver,
                extensionCapabilityCatalog: capabilityCatalog
            );
            var turnId = Guid.CreateVersion7();
            AcceptedTurn accepted = await acceptance.AcceptAsync(
                new TurnAcceptanceRequest(
                    UserId,
                    turnId,
                    new ExecutionTarget(agentId, AgentRuntimeType.Agent),
                    task.ProjectConversationId,
                    TurnPersistenceTestKit.CreateInput("hello"),
                    TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId),
                    Stream: true
                )
                {
                    Task = task,
                },
                token
            );
            try
            {
                Assert.Equal(extensionId, Assert.Single(accepted.Request.ExtensionPins).ExtensionId);
                if (durable)
                {
                    DurableExecutionRecord record = await kit.ReadExecutionAsync(turnId);
                    DurableExecutionManifest manifest =
                        DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
                            record.ManifestJson,
                            "durable execution manifest"
                        );
                    Assert.Equal([extensionId], manifest.ExtensionIds);
                    await using var recovered = await capabilityCatalog.AcquireForExecutionAsync(
                        manifest.ExtensionIds,
                        selection,
                        token
                    );
                    await AssertCompiledToolAsync(recovered, selection, token);
                }
                else
                {
                    cachedCapabilities = Assert.IsType<ExtensionCapabilitySnapshot>(
                        accepted.Request.Settings.ExtensionCapabilityLease
                    );
                    var firstTurnId = Guid.CreateVersion7();
                    await cachedCapabilities.BindTurnAsync(firstTurnId);
                    await AssertCompiledToolAsync(cachedCapabilities, selection, token);
                    await cachedCapabilities.ReleaseTurnAsync(firstTurnId);

                    var secondTurnId = Guid.CreateVersion7();
                    await using var nextCapabilities = await capabilityCatalog.AcquireCurrentAsync(selection, token);
                    await cachedCapabilities.RebindTurnAsync(secondTurnId, nextCapabilities);
                    await nextCapabilities.DisposeAsync();
                    await AssertCompiledToolAsync(cachedCapabilities, selection, token);
                    await cachedCapabilities.ReleaseTurnAsync(secondTurnId);
                }
                Assert.Empty(await context.AgentSkillRelations.IgnoreQueryFilters().ToArrayAsync(token));
                Assert.Empty(await context.ProjectSkillRelations.IgnoreQueryFilters().ToArrayAsync(token));
                Assert.Empty(await context.Skills.IgnoreQueryFilters().ToArrayAsync(token));

                IReadOnlyList<Guid> contributedSkillExtensions =
                    await capabilityCatalog.ListEnabledSkillExtensionIdsAsync(token);
                Assert.Equal([extensionId], contributedSkillExtensions);
                Assert.True(await extensionRuntime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), token));
                await using var optionalSkillSnapshot = await capabilityCatalog.AcquireCurrentAsync(
                    new ExtensionCapabilitySelection([], contributedSkillExtensions),
                    token
                );
                Assert.Empty(optionalSkillSnapshot.Skills);
                Assert.Empty(optionalSkillSnapshot.Pins);
                if (!durable)
                {
                    await extensionRuntime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), token);
                    await enablement.SetAsync(extensionId, false, token);
                    var reenabled = await enablement.SetAsync(extensionId, true, token);
                    var nextGeneration = await extensionRuntime.LoadAsync(
                        loadRequest with
                        {
                            Epoch = reenabled.Version,
                        },
                        token
                    );
                    await extensionRuntime.PrepareDatabaseAsync(nextGeneration.GenerationId, token);
                    await extensionRuntime.ActivateAsync(nextGeneration.GenerationId, token);
                    await extensionRuntime.ResumeAsync(nextGeneration.GenerationId, token);
                    await using var nextGenerationCapabilities = await capabilityCatalog.AcquireCurrentAsync(
                        selection,
                        token
                    );
                    Assert.False(cachedCapabilities!.Pins.SequenceEqual(nextGenerationCapabilities.Pins));
                    await Assert.ThrowsAsync<AgwException>(() =>
                        cachedCapabilities.RebindTurnAsync(Guid.CreateVersion7(), nextGenerationCapabilities).AsTask()
                    );
                    await nextGenerationCapabilities.DisposeAsync();
                    Assert.True(
                        await extensionRuntime.DrainAsync(nextGeneration.GenerationId, TimeSpan.FromSeconds(1), token)
                    );
                    await extensionRuntime.UnloadAsync(nextGeneration.GenerationId, TimeSpan.FromSeconds(5), token);
                }
                await accepted.Request.Settings.DisposeExtensionCapabilityLeaseAsync();
                if (!backgroundOnly)
                {
                    AcceptedTurn replay = await acceptance.AcceptAsync(
                        new TurnAcceptanceRequest(
                            UserId,
                            turnId,
                            new ExecutionTarget(agentId, AgentRuntimeType.Agent),
                            task.ProjectConversationId,
                            TurnPersistenceTestKit.CreateInput("hello"),
                            TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId),
                            Stream: true
                        )
                        {
                            Task = task,
                        },
                        token
                    );
                    Assert.False(replay.Created);
                    Assert.Equal(turnId, replay.Request.TurnId);
                    Assert.Single(await kit.ReadHistoryAsync(task.ProjectConversationId));
                    await replay.Request.Settings.DisposeExtensionCapabilityLeaseAsync();
                }
            }
            finally
            {
                await accepted.Request.Settings.DisposeExtensionCapabilityLeaseAsync();
            }
        }
        finally
        {
            if (Directory.Exists(extensionRoot))
            {
                Directory.Delete(extensionRoot, recursive: true);
            }
        }
    }

    private static async Task AssertCompiledToolAsync(
        ExtensionCapabilitySnapshot snapshot,
        ExtensionCapabilitySelection selection,
        CancellationToken cancellationToken
    )
    {
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(snapshot.SelectTools(selection)));
        var result = await function.InvokeAsync(
            new AIFunctionArguments { ["value"] = "background" },
            cancellationToken
        );
        Assert.Equal("current-calendar:background", JsonSerializer.SerializeToElement(result).GetString());
    }

    internal static async Task CreateCompiledExtensionPackageAsync(
        string extensionDirectory,
        Guid extensionId,
        CancellationToken cancellationToken
    )
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "ExtensionFixture");
        var packageDirectory = Path.Combine(extensionDirectory, "compiled-skill-provider");
        var files = Directory
            .GetFiles(fixtureDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => "server/" + Path.GetRelativePath(fixtureDirectory, path).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal
            );
        foreach (var (relativePath, contents) in files)
        {
            var path = Path.Combine(packageDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, contents, cancellationToken);
        }

        var manifestJson = JsonSerializer.Serialize(
            new
            {
                manifestVersion = 2,
                id = extensionId.ToString("D"),
                name = "compiled-skill-provider",
                displayName = "Compiled Skill Provider",
                version = "1.0.0",
                targets = 1,
                server = new
                {
                    apiVersion = 1,
                    entryAssembly = "server/Agw.Extensions.Runtime.TestExtension.dll",
                    entryType = "Agw.Extensions.Runtime.Tests.Fixture.CurrentTestExtension",
                },
                files = files.ToDictionary(
                    pair => pair.Key,
                    pair => Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
                    StringComparer.Ordinal
                ),
            }
        );
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "manifest.json"), manifestJson, cancellationToken);
    }

    /// <summary>
    /// 受理服务使用实际的 Projects 任务解析与 Agent 权限检查。
    /// An acceptance service over the real Projects task resolution and Agent permission check.
    /// </summary>
    private TurnAcceptanceService CreateResolvingAcceptance() =>
        _kit.CreateAcceptance(
            _kit.CreateProjectTasks(),
            new AgentExecutionFacadeTests.WorkspaceProjects(),
            permissions: new Agw.Agents.Definitions.Agents.ExecutionPermissionService(
                _kit.ResolveScoped<Agw.Agents.Application.Persistence.IAgentsDbContext>()
            )
        );

    private static Task<AcceptedTurn> AcceptForAsync(
        TurnAcceptanceService acceptance,
        Guid projectId,
        Guid conversationId,
        Guid agentId,
        string? extensionId
    ) =>
        acceptance.AcceptAsync(
            new TurnAcceptanceRequest(
                UserId,
                null,
                new ExecutionTarget(agentId, AgentRuntimeType.Agent),
                conversationId,
                TurnPersistenceTestKit.CreateInput("hello"),
                TurnPersistenceTestKit.CreateSettings(projectId, conversationId, extensionId),
                Stream: true
            ),
            TestContext.Current.CancellationToken
        );

    private async Task<Guid> SeedAgentAsync()
    {
        var agentId = Guid.CreateVersion7();
        await _kit.SeedAgentAsync(agentId);
        return agentId;
    }

    private TurnAcceptanceService CreateAcceptance(DurableExecutionCoordinator? durable = null) =>
        _kit.CreateAcceptance(projectTasks: null, new AgentExecutionFacadeTests.WorkspaceProjects(), durable);

    private async Task<AcceptedTurn> AcceptAsync(
        AgentExecutionTask task,
        Guid? turnId = null,
        DurableExecutionCoordinator? durable = null,
        AgwUserInput? input = null,
        string userId = UserId,
        TurnAcceptanceService? acceptance = null
    )
    {
        var agentId = Guid.CreateVersion7();
        await _kit.SeedAgentAsync(agentId, userId);
        return await (acceptance ?? CreateAcceptance(durable)).AcceptAsync(
            new TurnAcceptanceRequest(
                userId,
                turnId,
                new ExecutionTarget(agentId, AgentRuntimeType.Agent),
                task.ProjectConversationId,
                input ?? TurnPersistenceTestKit.CreateInput("hello"),
                TurnPersistenceTestKit.CreateSettings(task.ProjectId, task.ProjectConversationId),
                Stream: true
            )
            {
                Task = task,
            },
            TestContext.Current.CancellationToken
        );
    }
}
