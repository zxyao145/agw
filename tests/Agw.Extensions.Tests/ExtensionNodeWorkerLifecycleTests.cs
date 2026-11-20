using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Contracts.Messages;
using Agw.Auth.Extensions;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Runtime;
using Agw.Host.Data;
using Agw.Host.Runtime;
using Agw.Infrastructure;
using Agw.Infrastructure.Data;
using Agw.Settings;
using Agw.Settings.Contracts;
using Agw.Setup.Services;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Utils;
using Bens.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agw.Extensions.Tests;

public sealed class ExtensionNodeWorkerLifecycleTests
{
    [Fact]
    public async Task StopAsync_ControlPlaneExtension_ProvidesSkillsAndUnloadsRuntimeAndEndpoints()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "extension-worker-tests",
            Guid.CreateVersion7().ToString("N")
        );
        var contentRoot = Path.Combine(root, "content");
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(contentRoot);
        try
        {
            var activeExtensionId = Guid.CreateVersion7();
            var skillExtensionId = Guid.CreateVersion7();
            var blockedExtensionId = Guid.CreateVersion7();
            var blockingStopExtensionId = Guid.CreateVersion7();
            var continuationExtensionId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a100");
            await InstallExtensionAsync(
                contentRoot,
                activeExtensionId,
                "active",
                "Agw.Extensions.Runtime.Tests.Fixture.TestExtensionWithoutTools",
                TestContext.Current.CancellationToken
            );
            await InstallExtensionAsync(
                contentRoot,
                skillExtensionId,
                "skill",
                "Agw.Extensions.Runtime.Tests.Fixture.CurrentTestExtension",
                TestContext.Current.CancellationToken
            );
            await InstallExtensionAsync(
                contentRoot,
                blockedExtensionId,
                "blocked",
                "Agw.Extensions.Runtime.Tests.Fixture.BlockingStartExtension",
                TestContext.Current.CancellationToken
            );
            await InstallExtensionAsync(
                contentRoot,
                blockingStopExtensionId,
                "blocking-stop",
                "Agw.Extensions.Runtime.Tests.Fixture.BlockingStopExtension",
                TestContext.Current.CancellationToken
            );
            await InstallExtensionAsync(
                contentRoot,
                continuationExtensionId,
                "continuation",
                "Agw.Extensions.Runtime.Tests.Fixture.CurrentTestExtension",
                TestContext.Current.CancellationToken
            );

            var paths = AgwDataPaths.Resolve(contentRoot, dataRoot);
            Directory.CreateDirectory(paths.Root);
            Directory.CreateDirectory(paths.KeysDirectory);
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { ContentRootPath = contentRoot }
            );
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Database:ConnectionString"] =
                        $"Data Source={Path.Combine(paths.Root, "extension-worker-tests.db")};Pooling=False",
                    ["DistributedLock:Provider"] = "inmemory",
                    ["Extensions:Directory"] = "Extensions",
                }
            );
            builder.Services.AddSingleton(paths);
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDirectory));
            builder.Services.AddAuth();
            builder.Services.AddInfrastructure(builder.Configuration);
            builder.Services.AddExtensionInfrastructure();
            builder.Services.AddSettings();
            builder.Services.AddSetup(builder.Configuration);
            builder.Services.AddExtensions(builder.Configuration, "ControlPlane");
            builder.Services.AddScoped<IEntityAuditUserIdProvider, EntityAuditUserIdProvider>();

            await using var services = builder.Services.BuildServiceProvider();
            await using (var scope = services.CreateAsyncScope())
            {
                await scope
                    .ServiceProvider.GetRequiredService<AgwDbContext>()
                    .Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            }

            var enablement = services.GetRequiredService<IExtensionEnablementStore>();
            await enablement.SetAsync(activeExtensionId, true, TestContext.Current.CancellationToken);
            await enablement.SetAsync(skillExtensionId, true, TestContext.Current.CancellationToken);
            await enablement.SetAsync(blockedExtensionId, true, TestContext.Current.CancellationToken);
            await enablement.SetAsync(blockingStopExtensionId, true, TestContext.Current.CancellationToken);
            await enablement.SetAsync(continuationExtensionId, false, TestContext.Current.CancellationToken);
            Guid continuationExecutionId = await AddQueuedExecutionAsync(
                services,
                continuationExtensionId,
                TestContext.Current.CancellationToken
            );

            var worker = services.GetRequiredService<ExtensionNodeWorker>();
            var runtime = services.GetRequiredService<IExtensionRuntime>();
            var endpoints = services.GetRequiredService<ExtensionEndpointDataSource>();
            var workerStarted = false;
            try
            {
                await worker.StartAsync(TestContext.Current.CancellationToken);
                workerStarted = true;

                await services
                    .GetRequiredService<DatabaseInitializationStateStore>()
                    .PersistAsync("extension-worker-tests", TestContext.Current.CancellationToken);
                worker.RequestReconciliation(activeExtensionId);
                worker.RequestReconciliation(skillExtensionId);
                worker.RequestReconciliation(blockedExtensionId);
                worker.RequestReconciliation(blockingStopExtensionId);
                worker.RequestReconciliation(continuationExtensionId);

                var blockedDataDirectory = Path.Combine(
                    paths.Root,
                    "extensions",
                    "data",
                    blockedExtensionId.ToString("N")
                );
                await WaitForLifecycleStateAsync(
                    worker,
                    runtime,
                    endpoints,
                    activeExtensionId,
                    skillExtensionId,
                    blockedExtensionId,
                    Path.Combine(blockedDataDirectory, "start-waiting.marker"),
                    TestContext.Current.CancellationToken
                );
                await WaitForExtensionSnapshotAsync(
                    runtime,
                    continuationExtensionId,
                    ExtensionGenerationState.Ready,
                    TestContext.Current.CancellationToken
                );
                await WaitForExtensionSnapshotAsync(
                    runtime,
                    blockingStopExtensionId,
                    ExtensionGenerationState.Active,
                    TestContext.Current.CancellationToken
                );

                Assert.Contains(
                    runtime.GetSnapshots(),
                    snapshot =>
                        snapshot.GenerationId.ExtensionId == activeExtensionId
                        && snapshot.State == ExtensionGenerationState.Active
                );
                Assert.Contains(
                    runtime.GetSnapshots(),
                    snapshot =>
                        snapshot.GenerationId.ExtensionId == skillExtensionId
                        && snapshot.State == ExtensionGenerationState.Active
                        && snapshot.Skills.Any(skill => skill.Id == "calendar-skill")
                );
                Assert.NotEmpty(endpoints.Endpoints);
                await AssertEndpointRequestBodyAndErrorEnvelopeAsync(
                    services,
                    runtime,
                    endpoints,
                    activeExtensionId,
                    TestContext.Current.CancellationToken
                );
                await AssertDurableContinuationAvailableAsync(
                    runtime,
                    (IExtensionInvoker)runtime,
                    continuationExtensionId
                );

                ExtensionGenerationSnapshot blockingStopGeneration = Assert.Single(
                    runtime.GetSnapshots(),
                    snapshot => snapshot.GenerationId.ExtensionId == blockingStopExtensionId
                );
                Assert.True(
                    await runtime.DrainAsync(
                        blockingStopGeneration.GenerationId,
                        TimeSpan.FromSeconds(5),
                        TestContext.Current.CancellationToken
                    )
                );
                Task stoppingGeneration = runtime
                    .UnloadAsync(
                        blockingStopGeneration.GenerationId,
                        TimeSpan.FromSeconds(5),
                        TestContext.Current.CancellationToken
                    )
                    .AsTask();
                string blockingStopDirectory = Path.Combine(
                    paths.Root,
                    "extensions",
                    "data",
                    blockingStopExtensionId.ToString("N")
                );
                string stopWaitingMarker = Path.Combine(blockingStopDirectory, "stop-waiting.marker");
                string stopReleaseMarker = Path.Combine(blockingStopDirectory, "stop-release.marker");
                long stoppingStarted = TimeProvider.System.GetTimestamp();
                while (!File.Exists(stopWaitingMarker))
                {
                    Assert.True(TimeProvider.System.GetElapsedTime(stoppingStarted) < TimeSpan.FromSeconds(10));
                    await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
                }

                await enablement.SetAsync(blockingStopExtensionId, false, TestContext.Current.CancellationToken);
                ExtensionEnablementState reenabled = await enablement.SetAsync(
                    blockingStopExtensionId,
                    true,
                    TestContext.Current.CancellationToken
                );
                worker.RequestReconciliation(blockingStopExtensionId);
                stoppingStarted = TimeProvider.System.GetTimestamp();
                try
                {
                    while (worker.GetError(blockingStopExtensionId)?.Code != ErrorCodes.ExtensionOperationConflict.Code)
                    {
                        Assert.True(TimeProvider.System.GetElapsedTime(stoppingStarted) < TimeSpan.FromSeconds(10));
                        await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
                    }
                }
                finally
                {
                    await File.WriteAllTextAsync(stopReleaseMarker, "released", TestContext.Current.CancellationToken);
                    await stoppingGeneration;
                }

                worker.RequestReconciliation(blockingStopExtensionId);
                await WaitForExtensionSnapshotAsync(
                    runtime,
                    blockingStopExtensionId,
                    ExtensionGenerationState.Active,
                    TestContext.Current.CancellationToken
                );
                Assert.Equal(
                    reenabled.Version,
                    Assert
                        .Single(
                            runtime.GetSnapshots(),
                            snapshot => snapshot.GenerationId.ExtensionId == blockingStopExtensionId
                        )
                        .Epoch
                );
                Assert.Null(worker.GetError(blockingStopExtensionId));

                await worker.StopAsync(CancellationToken.None);
                workerStarted = false;

                Assert.Empty(runtime.GetSnapshots());
                Assert.Empty(endpoints.Endpoints);
                Assert.True(File.Exists(Path.Combine(blockedDataDirectory, "stopped.marker")));

                await enablement.SetAsync(activeExtensionId, false, TestContext.Current.CancellationToken);
                await enablement.SetAsync(skillExtensionId, false, TestContext.Current.CancellationToken);
                await enablement.SetAsync(blockedExtensionId, false, TestContext.Current.CancellationToken);
                ExtensionNodeWorker restartedWorker = ActivatorUtilities.CreateInstance<ExtensionNodeWorker>(services);
                var restartedWorkerStarted = false;
                try
                {
                    await restartedWorker.StartAsync(TestContext.Current.CancellationToken);
                    restartedWorkerStarted = true;
                    await WaitForExtensionSnapshotAsync(
                        runtime,
                        continuationExtensionId,
                        ExtensionGenerationState.Ready,
                        TestContext.Current.CancellationToken
                    );
                    await AssertDurableContinuationAvailableAsync(
                        runtime,
                        (IExtensionInvoker)runtime,
                        continuationExtensionId
                    );

                    await CompleteExecutionAsync(
                        services,
                        continuationExecutionId,
                        TestContext.Current.CancellationToken
                    );
                    restartedWorker.RequestReconciliation(continuationExtensionId);
                    await WaitForNoExtensionSnapshotAsync(
                        runtime,
                        continuationExtensionId,
                        TestContext.Current.CancellationToken
                    );

                    await restartedWorker.StopAsync(CancellationToken.None);
                    restartedWorkerStarted = false;
                }
                finally
                {
                    if (restartedWorkerStarted)
                    {
                        await restartedWorker.StopAsync(CancellationToken.None);
                    }
                }
            }
            finally
            {
                if (workerStarted)
                    await worker.StopAsync(CancellationToken.None);
            }

            static async Task WaitForLifecycleStateAsync(
                ExtensionNodeWorker worker,
                IExtensionRuntime runtime,
                ExtensionEndpointDataSource endpoints,
                Guid activeExtensionId,
                Guid skillExtensionId,
                Guid blockedExtensionId,
                string blockedStartMarker,
                CancellationToken cancellationToken
            )
            {
                var started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
                {
                    Assert.Null(worker.GetError(activeExtensionId));
                    Assert.Null(worker.GetError(blockedExtensionId));
                    if (
                        File.Exists(blockedStartMarker)
                        && runtime
                            .GetSnapshots()
                            .Any(snapshot =>
                                snapshot.GenerationId.ExtensionId == activeExtensionId
                                && snapshot.State == ExtensionGenerationState.Active
                            )
                        && runtime
                            .GetSnapshots()
                            .Any(snapshot =>
                                snapshot.GenerationId.ExtensionId == skillExtensionId
                                && snapshot.State == ExtensionGenerationState.Active
                                && snapshot.Skills.Any(skill => skill.Id == "calendar-skill")
                            )
                        && endpoints.Endpoints.Count > 0
                    )
                        return;
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                }
                Assert.Fail("The active and blocked extensions did not reach their expected lifecycle states.");
            }

            static async Task<Guid> AddQueuedExecutionAsync(
                IServiceProvider services,
                Guid extensionId,
                CancellationToken cancellationToken
            )
            {
                Guid turnId = Guid.CreateVersion7();
                Guid projectId = Guid.CreateVersion7();
                Guid conversationId = Guid.CreateVersion7();
                const string userId = "extension-worker-test";
                var manifest = new DurableExecutionManifest
                {
                    TurnId = turnId,
                    UserId = userId,
                    AgentId = Guid.CreateVersion7(),
                    AgentType = AgentRuntimeType.Agent,
                    Input = new AgwUserInput { Contents = [] },
                    Task = new DurableProjectTaskSnapshot
                    {
                        TaskId = Guid.CreateVersion7(),
                        ProjectConversationId = conversationId,
                        ProjectId = projectId,
                        ContextId = "extension-worker-continuation",
                    },
                    Settings = new DurableExecutionSettings { EnvironmentVariables = [], Resume = false },
                    WorkspaceSnapshot = ProjectWorkspacePaths.CreateSnapshot(projectId, null),
                    ExtensionIds = [extensionId],
                };

                await using AsyncServiceScope scope = services.CreateAsyncScope();
                AgwDbContext db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
                db.DurableExecutions.Add(
                    new DurableExecutionRecord
                    {
                        Id = turnId,
                        UserId = userId,
                        ProjectId = projectId,
                        ProjectConversationId = conversationId,
                        ScopeBackfilled = true,
                        ManifestJson = JsonUtil.Serialize(manifest),
                        Status = DurableExecutionStatus.Queued,
                        StateChangedAt = TimeProvider.System.GetUtcNow(),
                        StateVersion = Guid.CreateVersion7(),
                        CreateBy = userId,
                    }
                );
                await db.SaveChangesAsync(cancellationToken);
                return turnId;
            }

            static async Task AssertEndpointRequestBodyAndErrorEnvelopeAsync(
                IServiceProvider services,
                IExtensionRuntime runtime,
                ExtensionEndpointDataSource endpointDataSource,
                Guid extensionId,
                CancellationToken cancellationToken
            )
            {
                ISettingsStore settings = services.GetRequiredService<ISettingsStore>();
                if (
                    await settings.GetGlobalAsync<ExtensionDeploymentSettings>(
                        ExtensionDeploymentSettings.Key,
                        cancellationToken
                    )
                    is null
                )
                {
                    Assert.True(
                        await settings.TryCreateGlobalAsync(
                            ExtensionDeploymentSettings.Key,
                            new ExtensionDeploymentSettings { ServerId = Guid.CreateVersion7().ToString() },
                            cancellationToken
                        )
                    );
                }

                using IHost server = new HostBuilder()
                    .ConfigureWebHost(webHostBuilder =>
                    {
                        webHostBuilder.UseTestServer();
                        webHostBuilder.ConfigureServices(serverServices =>
                        {
                            serverServices.AddLogging();
                            serverServices.AddRouting();
                            serverServices.AddAuthorization(options =>
                                options.DefaultPolicy = new AuthorizationPolicyBuilder()
                                    .RequireAssertion(_ => true)
                                    .Build()
                            );
                            serverServices.AddApiResult();
                            serverServices.AddSingleton<IExtensionGenerationCatalog>(runtime);
                            serverServices.AddSingleton<IExtensionInvoker>((IExtensionInvoker)runtime);
                            serverServices.AddSingleton<IExtensionHostIdentity>(
                                services.GetRequiredService<IExtensionHostIdentity>()
                            );
                            serverServices.AddSingleton<IExtensionDeploymentIdentity>(
                                new ExtensionDeploymentIdentity(settings)
                            );
                        });
                        webHostBuilder.Configure(app =>
                        {
                            app.UseRouting();
                            app.UseAuthorization();
                            app.UseEndpoints(routeBuilder =>
                                routeBuilder.DataSources.Add(new RetainedEndpointDataSource(endpointDataSource))
                            );
                        });
                    })
                    .Build();
                await server.StartAsync(cancellationToken);
                using (HttpClient client = server.GetTestClient())
                {
                    var pipe = new Pipe();
                    await pipe.Writer.WriteAsync(
                        Encoding.UTF8.GetBytes("""{"value":"body-without-length"}"""),
                        cancellationToken
                    );
                    await pipe.Writer.CompleteAsync();
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ext/active/echo")
                    {
                        Version = HttpVersion.Version20,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        Content = new StreamContent(pipe.Reader.AsStream()),
                    };
                    request.Headers.Add("X-Agw-Extension-Id", extensionId.ToString());
                    Assert.Null(request.Content.Headers.ContentLength);
                    Assert.Empty(request.Headers.TransferEncoding);

                    using HttpResponseMessage echoResponse = await client.SendAsync(request, cancellationToken);
                    Assert.Equal(HttpStatusCode.OK, echoResponse.StatusCode);
                    using JsonDocument echo = JsonDocument.Parse(
                        await echoResponse.Content.ReadAsStringAsync(cancellationToken)
                    );
                    Assert.Equal(
                        "body-without-length",
                        echo.RootElement.GetProperty("data").GetProperty("value").GetString()
                    );

                    using var notFoundRequest = new HttpRequestMessage(HttpMethod.Get, "/api/ext/active/not-found");
                    notFoundRequest.Headers.Add("X-Agw-Extension-Id", extensionId.ToString());
                    using HttpResponseMessage notFoundResponse = await client.SendAsync(
                        notFoundRequest,
                        cancellationToken
                    );
                    Assert.Equal(HttpStatusCode.NotFound, notFoundResponse.StatusCode);
                    using JsonDocument notFound = JsonDocument.Parse(
                        await notFoundResponse.Content.ReadAsStringAsync(cancellationToken)
                    );
                    Assert.NotEqual(0, notFound.RootElement.GetProperty("code").GetInt32());
                    Assert.Equal("Not Found", notFound.RootElement.GetProperty("title").GetString());
                }
            }

            static async Task CompleteExecutionAsync(
                IServiceProvider services,
                Guid executionId,
                CancellationToken cancellationToken
            )
            {
                await using AsyncServiceScope scope = services.CreateAsyncScope();
                AgwDbContext db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
                DurableExecutionRecord execution = await db
                    .DurableExecutions.IgnoreQueryFilters()
                    .SingleAsync(record => record.Id == executionId, cancellationToken);
                execution.Status = DurableExecutionStatus.Completed;
                execution.StateChangedAt = TimeProvider.System.GetUtcNow();
                execution.StateVersion = Guid.CreateVersion7();
                await db.SaveChangesAsync(cancellationToken);
            }

            static async Task AssertDurableContinuationAvailableAsync(
                IExtensionRuntime runtime,
                IExtensionInvoker invoker,
                Guid extensionId
            )
            {
                AgwException unavailable = await Assert.ThrowsAsync<AgwException>(async () =>
                    await runtime.AcquireCurrentAsync(extensionId, TestContext.Current.CancellationToken)
                );
                Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, unavailable.Code);

                await using IExtensionGenerationLease lease = await runtime.AcquireForExecutionAsync(
                    extensionId,
                    TestContext.Current.CancellationToken
                );
                Assert.Equal(ExtensionGenerationState.Ready, lease.Snapshot.State);
                await using IExtensionInvocationResponse response = await invoker.InvokeSkillContentAsync(
                    lease,
                    new ExtensionSkillInvocation
                    {
                        ReleaseHash = lease.Pin.ReleaseHash,
                        Epoch = lease.Pin.Epoch,
                        SkillId = "calendar-skill",
                    },
                    TestContext.Current.CancellationToken
                );
                Assert.Equal(200, response.StatusCode);
                Assert.Equal("Current calendar instructions", response.JsonBody!.Value.GetString());
            }

            static async Task WaitForExtensionSnapshotAsync(
                IExtensionRuntime runtime,
                Guid extensionId,
                ExtensionGenerationState state,
                CancellationToken cancellationToken
            )
            {
                var started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
                {
                    if (
                        runtime
                            .GetSnapshots()
                            .Any(snapshot =>
                                snapshot.GenerationId.ExtensionId == extensionId && snapshot.State == state
                            )
                    )
                    {
                        return;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                }
                Assert.Fail($"Extension {extensionId} did not reach {state}.");
            }

            static async Task WaitForNoExtensionSnapshotAsync(
                IExtensionRuntime runtime,
                Guid extensionId,
                CancellationToken cancellationToken
            )
            {
                var started = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30))
                {
                    if (!runtime.GetSnapshots().Any(snapshot => snapshot.GenerationId.ExtensionId == extensionId))
                    {
                        return;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                }
                Assert.Fail($"Extension {extensionId} did not unload.");
            }

            static async Task InstallExtensionAsync(
                string contentRoot,
                Guid extensionId,
                string name,
                string entryType,
                CancellationToken cancellationToken
            )
            {
                var fixture = Path.Combine(AppContext.BaseDirectory, "ExtensionFixture");
                Assert.True(Directory.Exists(fixture), $"Compiled fixture was not found: {fixture}");
                var directory = Path.Combine(contentRoot, "Extensions", name);
                var files = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var source in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
                {
                    var relative = "server/" + Path.GetRelativePath(fixture, source).Replace('\\', '/');
                    var bytes = await File.ReadAllBytesAsync(source, cancellationToken);
                    var target = Path.Combine(directory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await File.WriteAllBytesAsync(target, bytes, cancellationToken);
                    files.Add(relative, Convert.ToHexStringLower(SHA256.HashData(bytes)));
                }
                var manifest = JsonSerializer.Serialize(
                    new
                    {
                        manifestVersion = 2,
                        id = extensionId.ToString("D"),
                        name,
                        displayName = name,
                        version = "1.0.0",
                        targets = (int)ExtensionTarget.Server,
                        server = new
                        {
                            apiVersion = 1,
                            entryAssembly = "server/Agw.Extensions.Runtime.TestExtension.dll",
                            entryType,
                        },
                        files,
                    }
                );
                await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), manifest, cancellationToken);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RetainedEndpointDataSource(EndpointDataSource source) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints => source.Endpoints;

        public override Microsoft.Extensions.Primitives.IChangeToken GetChangeToken() => source.GetChangeToken();
    }
}
