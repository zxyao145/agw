using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Agents.Application.Persistence;
using Agw.Agents.Contracts.Execution;
using Agw.Agents.Contracts.Messages;
using Agw.Agents.Definitions.Agents;
using Agw.Agents.Definitions.Domain.Services;
using Agw.Agents.Definitions.Persistence;
using Agw.Agents.Execution;
using Agw.Agents.Execution.Agents.Composition;
using Agw.Auth.Application;
using Agw.Auth.Contracts;
using Agw.Extensions.Application.Packages;
using Agw.Extensions.Application.Persistence;
using Agw.Extensions.Contracts;
using Agw.Extensions.Infrastructure.Packages;
using Agw.Extensions.Infrastructure.Runtime;
using Agw.Host.Runtime;
using Agw.Infrastructure;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Settings;
using Agw.Integrations.Application.Facades;
using Agw.Integrations.Application.Persistence;
using Agw.Integrations.Contracts.Capabilities;
using Agw.Integrations.Extensions;
using Agw.Integrations.Mcp;
using Agw.Projects;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Runtime;
using Agw.Providers.Application.Facades;
using Agw.Settings;
using Agw.Settings.Application.Persistence;
using Agw.Settings.Contracts;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Contracts.Events;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Shared.Tooling;
using Agw.Shared.Utils;
using Agw.Skills.Application.Facades;
using Agw.Tools;
using Agw.Tools.Abstractions;
using Agw.Tools.Contracts;
using Agw.Tools.Runtime;
using Agw.Tools.ToolBlocks;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Agw.Extensions.Runtime.Tests;

[CollectionDefinition(ExtensionRuntimeTestCollection.Name, DisableParallelization = true)]
public sealed class ExtensionRuntimeTestCollection
{
    public const string Name = "ExtensionRuntime";
}

[Collection(ExtensionRuntimeTestCollection.Name)]
public sealed class ExtensionRuntimeTests
{
    private static readonly Guid ExtensionId = Guid.Parse("019a4c17-5f00-7a80-8b3d-2b732f48a100");

    [Fact]
    public async Task CallbackHttp_RawBodyAndHeaders_ReachCurrentGenerationWithProtocolResponse()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        await LoadAndActivateAsync(host);
        using var dataSource = new ExtensionEndpointDataSource();
        dataSource.Publish(host.Runtime.GetSnapshots);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IExtensionGenerationCatalog>(host.Runtime);
        builder.Services.AddSingleton<IExtensionInvoker>(host.Runtime);
        builder.Services.AddSingleton(host.Services.GetRequiredService<IExtensionDeploymentIdentity>());
        builder.Services.AddSingleton(host.Services.GetRequiredService<IExtensionHostIdentity>());
        builder.Services.AddHttpClient();
        await using var app = builder.Build();
        ((IEndpointRouteBuilder)app).DataSources.Add(dataSource);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            string address = app
                .Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
            const string body = "{ \"value\": \"signed payload\" }";
            using var request = new HttpRequestMessage(HttpMethod.Post, address + "/api/ext/runtime-test/callback")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Callback-Signature", "signature");
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            );
            Assert.Equal(
                "signature:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
                result.RootElement.GetProperty("value").GetString()
            );
            Assert.False(result.RootElement.TryGetProperty("code", out _));
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task BrowserPageHttp_UsesAuthorizationMetadataAndSkipsExtensionVersionHeader()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        await LoadAndActivateAsync(host);
        using var dataSource = new ExtensionEndpointDataSource();
        dataSource.Publish(host.Runtime.GetSnapshots);
        string endpointNames = string.Join(", ", dataSource.Endpoints.Select(endpoint => endpoint.DisplayName));
        var browserPage = dataSource.Endpoints.SingleOrDefault(endpoint =>
            endpoint.DisplayName == "runtime-test: browser-page"
        );
        Assert.True(browserPage is not null, endpointNames);
        Assert.NotNull(browserPage!.Metadata.GetMetadata<AuthorizeAttribute>());
        Assert.Null(browserPage.Metadata.GetMetadata<AllowAnonymousAttribute>());
        var callback = dataSource.Endpoints.Single(endpoint => endpoint.DisplayName == "runtime-test: callback");
        Assert.NotNull(callback.Metadata.GetMetadata<AllowAnonymousAttribute>());

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Services.AddDataProtection();
        builder
            .Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }
            );
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IExtensionGenerationCatalog>(host.Runtime);
        builder.Services.AddSingleton<IExtensionInvoker>(host.Runtime);
        builder.Services.AddSingleton(host.Services.GetRequiredService<IExtensionDeploymentIdentity>());
        builder.Services.AddSingleton(host.Services.GetRequiredService<IExtensionHostIdentity>());
        builder.Services.AddHttpClient();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        ((IEndpointRouteBuilder)app).DataSources.Add(dataSource);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            string address = app
                .Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();
            using var unauthenticatedResponse = await client.GetAsync(
                address + "/api/ext/runtime-test/browser",
                TestContext.Current.CancellationToken
            );
            Assert.Equal(StatusCodes.Status401Unauthorized, (int)unauthenticatedResponse.StatusCode);

            var signInContext = new DefaultHttpContext { RequestServices = app.Services };
            var user = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "browser-page-user")],
                    CookieAuthenticationDefaults.AuthenticationScheme
                )
            );
            await app
                .Services.GetRequiredService<IAuthenticationService>()
                .SignInAsync(
                    signInContext,
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    user,
                    new AuthenticationProperties()
                );
            string cookie = signInContext.Response.Headers.SetCookie.ToString();
            string cookieValue = cookie[..cookie.IndexOf(';')];

            using var browserRequest = new HttpRequestMessage(
                HttpMethod.Get,
                address + "/api/ext/runtime-test/browser"
            );
            browserRequest.Headers.Add("Cookie", cookieValue);
            using var browserResponse = await client.SendAsync(browserRequest, TestContext.Current.CancellationToken);
            browserResponse.EnsureSuccessStatusCode();
            Assert.Equal("text/html", browserResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal(
                "<html><body>Signed in</body></html>",
                await browserResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            );

            using var missingVersionHeader = new HttpRequestMessage(
                HttpMethod.Get,
                address + "/api/ext/runtime-test/stream"
            );
            missingVersionHeader.Headers.Add("Cookie", cookieValue);
            using var missingVersionResponse = await client.SendAsync(
                missingVersionHeader,
                TestContext.Current.CancellationToken
            );
            Assert.False(missingVersionResponse.IsSuccessStatusCode);
            using var currentVersionRequest = new HttpRequestMessage(
                HttpMethod.Get,
                address + "/api/ext/runtime-test/stream"
            );
            currentVersionRequest.Headers.Add("Cookie", cookieValue);
            currentVersionRequest.Headers.Add("X-Agw-Extension-Id", ExtensionId.ToString());
            using var currentVersionResponse = await client.SendAsync(
                currentVersionRequest,
                TestContext.Current.CancellationToken
            );
            currentVersionResponse.EnsureSuccessStatusCode();
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task InvokeMessagingHandlers_UsesLeasedGenerationAndCallerIdentity()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        Assert.Contains(generation.EventHandlers, handler => handler.Id == "caller");
        Assert.Contains(generation.EventSourceProviders, provider => provider.Id == "source");
        Assert.Contains(generation.NotificationChannels, channel => channel.Id == "channel");

        await using IExtensionGenerationLease lease = await runtime.AcquireCurrentAsync(
            ExtensionId,
            TestContext.Current.CancellationToken
        );
        Guid deliveryId = Guid.CreateVersion7();
        var delivery = new ExtensionEventDelivery
        {
            Event = new CloudEventEnvelope
            {
                Id = "event-1",
                Source = "/test",
                Type = "test.event",
            },
            DeliveryId = deliveryId,
            Attempt = 1,
            SubscriptionId = Guid.CreateVersion7(),
            EventId = Guid.CreateVersion7(),
            SourceId = Guid.CreateVersion7(),
            ReceivedAt = DateTimeOffset.UnixEpoch,
            ChainId = Guid.CreateVersion7(),
            Hops = 0,
        };
        ExtensionEventHandlerResult handlerResult = await runtime.InvokeEventHandlerAsync(
            lease,
            new ExtensionEventHandlerInvocation
            {
                HandlerId = "caller",
                UserId = "handler-user",
                Delivery = delivery,
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ExtensionEventHandlerResultKind.Failed, handlerResult.Kind);
        Assert.Equal(
            $"{ExtensionId:D}|{generation.GenerationId.GenerationId:D}|handler-user|{deliveryId:D}|{deliveryId:D}",
            handlerResult.Reason
        );

        ExtensionSourcePollResult pollResult = await runtime.InvokeSourcePollAsync(
            lease,
            new ExtensionSourcePollInvocation
            {
                ProviderId = "source",
                UserId = "source-user",
                InvocationId = Guid.CreateVersion7(),
                Request = new ExtensionSourcePollRequest
                {
                    SourceId = Guid.CreateVersion7(),
                    Config = JsonSerializer.SerializeToElement(new { enabled = true }),
                    Baseline = false,
                },
            },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("source-user", Assert.Single(pollResult.Events).Subject);

        ExtensionNotificationChannelResult targetsResult = await runtime.InvokeNotificationChannelAsync(
            lease,
            new ExtensionNotificationChannelInvocation
            {
                ChannelId = "channel",
                UserId = "channel-user",
                InvocationId = Guid.CreateVersion7(),
                Operation = ExtensionNotificationChannelOperation.ListTargets,
            },
            TestContext.Current.CancellationToken
        );
        Assert.Equal("channel-user", Assert.Single(targetsResult.Targets!).Name);

        var message = new NotificationMessage
        {
            NotificationId = Guid.CreateVersion7(),
            DispatchKey = "dispatch",
            ChannelKey = "channel",
            IdempotencyKey = "dispatch:1",
            ContentVersion = 1,
            Kind = ExtensionNotificationKind.Notice,
            State = ExtensionNotificationState.Open,
            Title = "Notice",
            Body = "Body",
        };
        ExtensionNotificationChannelResult sendResult = await runtime.InvokeNotificationChannelAsync(
            lease,
            new ExtensionNotificationChannelInvocation
            {
                ChannelId = "channel",
                UserId = "channel-user",
                InvocationId = Guid.CreateVersion7(),
                Operation = ExtensionNotificationChannelOperation.Send,
                Message = message,
            },
            TestContext.Current.CancellationToken
        );
        Assert.Equal(NotificationSendStatus.Sent, sendResult.SendResult!.Status);
        Assert.Equal($"{message.NotificationId:D}:channel-user", sendResult.SendResult.ExternalMessageId);

        ExtensionNotificationChannelResult updateResult = await runtime.InvokeNotificationChannelAsync(
            lease,
            new ExtensionNotificationChannelInvocation
            {
                ChannelId = "channel",
                UserId = "channel-user",
                InvocationId = Guid.CreateVersion7(),
                Operation = ExtensionNotificationChannelOperation.Update,
                Message = message with { ContentVersion = 2 },
                ExternalMessageId = sendResult.SendResult.ExternalMessageId,
            },
            TestContext.Current.CancellationToken
        );
        Assert.Equal(sendResult.SendResult.ExternalMessageId, updateResult.SendResult!.ExternalMessageId);

        Assert.Null(host.Services.GetRequiredService<IExtensionCallerAccessor>().Current);
    }

    [Fact]
    public async Task InvokeSdkCapabilities_UsesApplicationLeaseAndExtensionScopedSecrets()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        await using IExtensionGenerationLease lease = await runtime.AcquireCurrentAsync(
            ExtensionId,
            TestContext.Current.CancellationToken
        );

        JsonElement? result = await InvokeEndpointAsync(runtime, lease, "sdk-capabilities");

        Assert.True(result.HasValue);
        Assert.False(result.Value.GetProperty("leaseLost").GetBoolean());
        Assert.True(result.Value.GetProperty("secretRoundTrips").GetBoolean());
        Assert.Equal(generation.GenerationId, lease.Snapshot.GenerationId);
    }

    [Fact]
    public async Task Load_PreservesDeclaredEventPermissionsInGenerationSnapshot()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionGenerationSnapshot generation = await host.Runtime.LoadAsync(
            CreateLoadRequest(host) with
            {
                EventPermissions = new ExtensionEventPermissions
                {
                    Publish = ["com.example.issue.*"],
                    PublishRoot = true,
                },
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(["com.example.issue.*"], generation.EventPermissions.Publish);
        Assert.True(generation.EventPermissions.PublishRoot);
    }

    [Fact]
    public void ParseManifest_ReservedEventPublishPatterns_AreRejected()
    {
        string manifest = JsonSerializer.Serialize(
            new
            {
                manifestVersion = 2,
                id = ExtensionId.ToString("D"),
                name = "runtime-test",
                displayName = "Runtime Test",
                version = "1.0.0",
                targets = (int)ExtensionTarget.Server,
                server = new
                {
                    apiVersion = 1,
                    entryAssembly = "server/extension.dll",
                    entryType = "Test.Extension",
                    permissions = new { events = new { publish = new[] { "agw.*" }, publishRoot = true } },
                },
                files = new Dictionary<string, string> { ["server/extension.dll"] = new string('a', 64) },
            }
        );

        AgwException exception = Assert.Throws<AgwException>(() => ExtensionManifestReader.Parse(manifest));

        Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, exception.Code);
    }

    [Fact]
    public async Task Constructor_MissingHostIdentity_FailsFast()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        var services = new ServiceCollection();
        services.AddOptions<ExtensionOptions>();
        using ServiceProvider hostServices = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() =>
            new ExtensionRuntime(
                hostServices,
                host.Services.GetRequiredService<IExtensionRuntimeAvailability>(),
                TimeProvider.System,
                NullLogger<ExtensionRuntime>.Instance,
                NullLoggerFactory.Instance
            )
        );
    }

    [Fact]
    public async Task InvokeEndpoint_ExtensionPrivateException_ReturnsStableAgwException()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        IExtensionGenerationLease generationLease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );

        AgwException exception = await Assert.ThrowsAsync<AgwException>(async () =>
        {
            await runtime.InvokeEndpointAsync(
                generationLease,
                new ExtensionEndpointInvocation
                {
                    ReleaseHash = generation.ReleaseHash,
                    Epoch = generation.Epoch,
                    EndpointId = "error",
                    Request = JsonDocument.Parse("""{"method":"GET","query":{},"body":null}""").RootElement.Clone(),
                },
                CancellationToken.None
            );
        });

        Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, exception.Code);
        await generationLease.DisposeAsync();
        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task LoadAndActivate_RegistersEndpointAndInvokesIt()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        await using IExtensionGenerationLease generationLease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );

        await using IExtensionInvocationResponse response = await runtime.InvokeEndpointAsync(
            generationLease,
            new ExtensionEndpointInvocation
            {
                ReleaseHash = generation.ReleaseHash,
                Epoch = generation.Epoch,
                EndpointId = "echo",
                Request = JsonDocument
                    .Parse("""{"method":"POST","query":{},"body":{"value":"extension"}}""")
                    .RootElement.Clone(),
            },
            CancellationToken.None
        );

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", response.ContentType);
        Assert.Equal("extension", response.JsonBody!.Value.GetProperty("value").GetString());
        await response.CompleteAsync();
        await generationLease.DisposeAsync();

        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task InvokeEndpoint_UnregisteredRuntimeCapability_IsDenied()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        Assert.Same(runtime, host.Services.GetRequiredService<IExtensionRuntime>());
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        await using IExtensionGenerationLease generationLease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );

        AgwException exception = await Assert.ThrowsAsync<AgwException>(async () =>
        {
            await runtime.InvokeEndpointAsync(
                generationLease,
                new ExtensionEndpointInvocation
                {
                    ReleaseHash = generation.ReleaseHash,
                    Epoch = generation.Epoch,
                    EndpointId = "runtime-capability",
                    Request = JsonDocument.Parse("""{"method":"GET","query":{},"body":null}""").RootElement.Clone(),
                },
                CancellationToken.None
            );
        });

        Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, exception.Code);
        await generationLease.DisposeAsync();
        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Theory]
    [InlineData("InvalidEndpointEmptySegmentExtension")]
    [InlineData("InvalidEndpointEncodedTraversalExtension")]
    [InlineData("InvalidEndpointColonExtension")]
    [InlineData("InvalidEndpointControlCharacterExtension")]
    [InlineData("InvalidEndpointNullPathExtension")]
    public async Task LoadAsync_InvalidEndpointPath_IsRejectedAndUnloadConfirmed(string entryTypeName)
    {
        string entryType = $"Agw.Extensions.Runtime.Tests.Fixture.{entryTypeName}";
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(entryType: entryType);
        AgwException exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await host.Runtime.LoadAsync(CreateLoadRequest(host, entryType), CancellationToken.None)
        );

        Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, exception.Code);
        Assert.Empty(host.Runtime.GetSnapshots());
    }

    [Fact]
    public async Task LoadAsync_BrowserPageWithNonGetMethod_IsRejectedAndUnloadConfirmed()
    {
        const string entryType = "Agw.Extensions.Runtime.Tests.Fixture.InvalidBrowserPageMethodExtension";
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(entryType: entryType);
        AgwException exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await host.Runtime.LoadAsync(CreateLoadRequest(host, entryType), CancellationToken.None)
        );

        Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, exception.Code);
        Assert.Empty(host.Runtime.GetSnapshots());
    }

    [Theory]
    [InlineData("InvalidEndpointSchemaExtension")]
    [InlineData("InvalidToolSchemaExtension")]
    public async Task LoadAsync_InvalidEndpointOrToolSchema_IsRejectedAndUnloadConfirmed(string entryTypeName)
    {
        string entryType = $"Agw.Extensions.Runtime.Tests.Fixture.{entryTypeName}";
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(entryType: entryType);
        AgwException exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await host.Runtime.LoadAsync(CreateLoadRequest(host, entryType), CancellationToken.None)
        );

        Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, exception.Code);
        Assert.Empty(host.Runtime.GetSnapshots());
    }

    [Theory]
    [InlineData("InvalidSkillNameExtension")]
    [InlineData("InvalidSkillDescriptionExtension")]
    [InlineData("DuplicateSkillNameExtension")]
    public async Task LoadAsync_InvalidSkillMetadata_IsRejectedAndUnloadConfirmed(string entryTypeName)
    {
        string entryType = $"Agw.Extensions.Runtime.Tests.Fixture.{entryTypeName}";
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(entryType: entryType);
        AgwException exception = await Assert.ThrowsAsync<AgwException>(async () =>
            await host.Runtime.LoadAsync(CreateLoadRequest(host, entryType), CancellationToken.None)
        );

        Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, exception.Code);
        Assert.Empty(host.Runtime.GetSnapshots());
    }

    [Fact]
    public async Task LoadAsync_FailedLoadUnloadTimeout_PreservesLoadErrorAndTracksUnloadFailure()
    {
        const string entryType = "Agw.Extensions.Runtime.Tests.Fixture.BlockingLoadFailureExtension";
        string waitingPath = Path.Combine(AppContext.BaseDirectory, "extension-load-failure-waiting.marker");
        string releasePath = Path.Combine(AppContext.BaseDirectory, "extension-load-failure-release.marker");
        File.Delete(waitingPath);
        File.Delete(releasePath);
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(
            entryType: entryType,
            unloadTimeout: TimeSpan.FromMilliseconds(100)
        );
        Task<ExtensionGenerationSnapshot> loadTask = Task.Run(() =>
            host.Runtime.LoadAsync(CreateLoadRequest(host, entryType), TestContext.Current.CancellationToken).AsTask()
        );

        try
        {
            long started = TimeProvider.System.GetTimestamp();
            while (!File.Exists(waitingPath))
            {
                Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
                await Task.Delay(TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
            }

            RetainedLoadContext retained = CaptureCollectibleContext();
            await File.WriteAllTextAsync(releasePath, "released", TestContext.Current.CancellationToken);
            AgwException loadFailure = await Assert.ThrowsAsync<AgwException>(async () => await loadTask);

            Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, loadFailure.Code);
            Assert.Equal(ExtensionGenerationState.UnloadFailed, Assert.Single(host.Runtime.GetSnapshots()).State);

            retained.Context = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await host.Runtime.UnloadAsync(
                Assert.Single(host.Runtime.GetSnapshots()).GenerationId,
                TimeSpan.Zero,
                TestContext.Current.CancellationToken
            );
            Assert.Empty(host.Runtime.GetSnapshots());
        }
        finally
        {
            File.WriteAllText(releasePath, "released");
            if (!loadTask.IsCompleted)
            {
                await loadTask;
            }
            File.Delete(waitingPath);
            File.Delete(releasePath);
        }
    }

    [Fact]
    public async Task LoadAsync_NonPlatformSystemDependency_UsesExtensionPackageAssembly()
    {
        string entryType = "Agw.Extensions.Runtime.Tests.Fixture.SystemIoHashingExtension";
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(entryType: entryType);
        ExtensionGenerationSnapshot generation = await host.Runtime.LoadAsync(
            CreateLoadRequest(host, entryType),
            CancellationToken.None
        );

        AssertPrivateSystemDependencyLoaded();
        await host.Runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task LoadAsync_HostDependencyWithExtensionCopy_LoadsExtensionAssembly()
    {
        const string entryType = "Agw.Extensions.Runtime.Tests.Fixture.SemverExtension";
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(entryType: entryType);
        ExtensionGenerationSnapshot generation = await host.Runtime.LoadAsync(
            CreateLoadRequest(host, entryType),
            TestContext.Current.CancellationToken
        );

        AssertPrivateAssemblyLoaded("Semver");
        await host.Runtime.UnloadAsync(
            generation.GenerationId,
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken
        );
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertPrivateAssemblyLoaded(string assemblyName)
    {
        AssemblyLoadContext extensionLoadContext = Assert.Single(
            AssemblyLoadContext.All,
            loadContext =>
                loadContext.IsCollectible
                && loadContext.Name?.StartsWith("Agw.Extension.", StringComparison.Ordinal) == true
        );
        Assert.Contains(extensionLoadContext.Assemblies, assembly => assembly.GetName().Name == assemblyName);
        Assert.Contains(AssemblyLoadContext.Default.Assemblies, assembly => assembly.GetName().Name == assemblyName);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertPrivateSystemDependencyLoaded()
    {
        AssemblyLoadContext extensionLoadContext = Assert.Single(
            AssemblyLoadContext.All,
            loadContext => loadContext.IsCollectible
        );
        Assert.Contains(extensionLoadContext.Assemblies, assembly => assembly.GetName().Name == "System.IO.Hashing");
        Assert.DoesNotContain(
            extensionLoadContext.Assemblies,
            assembly => assembly.GetName().Name == "Agw.Extensions.Sdk"
        );
        Assert.Contains(
            AssemblyLoadContext.Default.Assemblies,
            assembly => assembly.GetName().Name == "Agw.Extensions.Sdk"
        );
    }

    [Fact]
    public async Task UnloadAsync_ReferenceReleasedAfterInitialCollections_ConfirmsUnload()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        var generation = await host.Runtime.LoadAsync(CreateLoadRequest(host), TestContext.Current.CancellationToken);
        var retained = CaptureCollectibleContext();
        var unloading = host
            .Runtime.UnloadAsync(
                generation.GenerationId,
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken
            )
            .AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);
        Assert.False(unloading.IsCompleted);
        retained.Context = null;
        await unloading;
        Assert.Empty(host.Runtime.GetSnapshots());
    }

    [Fact]
    public async Task UnloadAsync_UnloadFailedRetry_DoesNotRepeatForcedCollections()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        var generation = await host.Runtime.LoadAsync(CreateLoadRequest(host), TestContext.Current.CancellationToken);
        RetainedLoadContext retained = CaptureCollectibleContext();

        AgwException initialFailure = await Assert.ThrowsAsync<AgwException>(async () =>
            await host.Runtime.UnloadAsync(
                generation.GenerationId,
                TimeSpan.Zero,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(ErrorCodes.ExtensionUnloadFailed.Code, initialFailure.Code);

        long collectionsBeforeRetry = GC.CollectionCount(2);
        AgwException retryFailure = await Assert.ThrowsAsync<AgwException>(async () =>
            await host.Runtime.UnloadAsync(
                generation.GenerationId,
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(ErrorCodes.ExtensionUnloadFailed.Code, retryFailure.Code);
        Assert.Equal(collectionsBeforeRetry, GC.CollectionCount(2));
        Assert.Equal(ExtensionGenerationState.UnloadFailed, Assert.Single(host.Runtime.GetSnapshots()).State);

        retained.Context = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await host.Runtime.UnloadAsync(generation.GenerationId, TimeSpan.Zero, TestContext.Current.CancellationToken);
        Assert.Empty(host.Runtime.GetSnapshots());
    }

    [Fact]
    public async Task StopAsync_UnloadFailure_ContinuesUnloadingOtherGenerations()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        Guid retainedExtensionId = Guid.CreateVersion7();
        Guid otherExtensionId = Guid.CreateVersion7();
        ExtensionRuntimeLoadRequest retainedRequest = CreateLoadRequest(host) with
        {
            ExtensionId = retainedExtensionId,
            DataDirectory = Path.Combine(host.DataDirectory, retainedExtensionId.ToString("N")),
        };
        ExtensionGenerationSnapshot retainedGeneration = await runtime.LoadAsync(
            retainedRequest,
            TestContext.Current.CancellationToken
        );
        RetainedLoadContext retained = CaptureCollectibleContext();
        AgwException unloadFailure = await Assert.ThrowsAsync<AgwException>(async () =>
            await runtime.UnloadAsync(
                retainedGeneration.GenerationId,
                TimeSpan.Zero,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(ErrorCodes.ExtensionUnloadFailed.Code, unloadFailure.Code);

        ExtensionGenerationSnapshot otherGeneration = await runtime.LoadAsync(
            CreateLoadRequest(host) with
            {
                ExtensionId = otherExtensionId,
                DataDirectory = Path.Combine(host.DataDirectory, otherExtensionId.ToString("N")),
            },
            TestContext.Current.CancellationToken
        );

        AgwException stopFailure = await Assert.ThrowsAsync<AgwException>(async () =>
            await runtime.StopAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(ErrorCodes.ExtensionUnloadFailed.Code, stopFailure.Code);
        Assert.Contains(
            runtime.GetSnapshots(),
            snapshot =>
                snapshot.GenerationId == retainedGeneration.GenerationId
                && snapshot.State == ExtensionGenerationState.UnloadFailed
        );
        Assert.DoesNotContain(
            runtime.GetSnapshots(),
            snapshot => snapshot.GenerationId == otherGeneration.GenerationId
        );

        retained.Context = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(runtime.GetSnapshots());
    }

    [Fact]
    public async Task DisposeAsync_AfterCanceledStop_UnloadsOwnedGeneration()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        IExtensionGenerationLease lease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            TestContext.Current.CancellationToken
        );
        using var stopCancellation = new CancellationTokenSource();
        Task stopTask = runtime.StopAsync(stopCancellation.Token).AsTask();
        long started = TimeProvider.System.GetTimestamp();
        while (Assert.Single(runtime.GetSnapshots()).State != ExtensionGenerationState.Draining)
        {
            Assert.True(TimeProvider.System.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
            await Task.Delay(TimeSpan.FromMilliseconds(1), TestContext.Current.CancellationToken);
        }

        stopCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopTask);
        await lease.DisposeAsync();

        Assert.Equal(ExtensionGenerationState.Faulted, Assert.Single(runtime.GetSnapshots()).State);
        await runtime.DisposeAsync();

        Assert.Empty(runtime.GetSnapshots());
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static RetainedLoadContext CaptureCollectibleContext() =>
        new() { Context = Assert.Single(AssemblyLoadContext.All, context => context.IsCollectible) };

    private sealed class RetainedLoadContext
    {
        public AssemblyLoadContext? Context;
    }

    [Fact]
    public async Task AcquireCurrentAsync_ReadyGenerationAfterPublication_DoesNotResume()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(extensionActive: false);
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadPreparedAsync(host);
        await runtime.ActivateAsync(generation.GenerationId, CancellationToken.None);

        AgwException unavailable = await Assert.ThrowsAsync<AgwException>(async () =>
            await runtime.AcquireCurrentAsync(generation.GenerationId.ExtensionId, CancellationToken.None)
        );
        Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, unavailable.Code);
        Assert.Equal(ExtensionGenerationState.Ready, Assert.Single(runtime.GetSnapshots()).State);

        await host.PublishExtensionAsync();
        AgwException? afterPublicationFailure = null;
        IExtensionGenerationLease? unexpectedLease = null;
        try
        {
            unexpectedLease = await runtime.AcquireCurrentAsync(
                generation.GenerationId.ExtensionId,
                CancellationToken.None
            );
        }
        catch (AgwException exception)
        {
            afterPublicationFailure = exception;
        }
        finally
        {
            if (unexpectedLease != null)
            {
                await unexpectedLease.DisposeAsync();
            }
        }

        Assert.NotNull(afterPublicationFailure);
        Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, afterPublicationFailure.Code);
        Assert.Equal(ExtensionGenerationState.Ready, Assert.Single(runtime.GetSnapshots()).State);

        await runtime.ResumeAsync(generation.GenerationId, CancellationToken.None);
        await using IExtensionGenerationLease lease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );
        Assert.Equal(generation.GenerationId, lease.Snapshot.GenerationId);
        Assert.Equal(ExtensionGenerationState.Active, Assert.Single(runtime.GetSnapshots()).State);
        await lease.DisposeAsync();

        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Theory]
    [InlineData("ControlPlane", true)]
    [InlineData("Standalone", true)]
    [InlineData("DataPlane", false)]
    public async Task ActivateAsync_BackgroundServiceStartsOnlyOnSchedulingHosts(string hostRole, bool expectedStarted)
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(hostRole);
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);

        Assert.Equal(expectedStarted, File.Exists(Path.Combine(host.DataDirectory, "background-started.txt")));
        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task DrainAsync_ResponseLeaseExceedsTimeout_ContinuesUntilResponseCompletes()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        IExtensionGenerationLease generationLease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );
        IExtensionInvocationResponse response = await runtime.InvokeEndpointAsync(
            generationLease,
            new ExtensionEndpointInvocation
            {
                ReleaseHash = generation.ReleaseHash,
                Epoch = generation.Epoch,
                EndpointId = "stream",
                Request = JsonDocument.Parse("""{"method":"GET","query":{},"body":null}""").RootElement.Clone(),
            },
            CancellationToken.None
        );
        await generationLease.DisposeAsync();
        await using var streamEnumerator = response.BodyStream!.GetAsyncEnumerator();
        Assert.True(await streamEnumerator.MoveNextAsync());

        Assert.False(
            await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromMilliseconds(20), CancellationToken.None)
        );
        Assert.Equal(ExtensionGenerationState.Draining, Assert.Single(runtime.GetSnapshots()).State);
        AgwException unavailable = await Assert.ThrowsAsync<AgwException>(async () =>
            await runtime.AcquireCurrentAsync(
                generation.GenerationId.ExtensionId,
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, unavailable.Code);
        Assert.True(await streamEnumerator.MoveNextAsync());
        Assert.Equal("{\"chunk\":2}", Encoding.UTF8.GetString(streamEnumerator.Current.Span));
        Assert.False(await streamEnumerator.MoveNextAsync());
        await streamEnumerator.DisposeAsync();
        await response.DisposeAsync();

        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task DrainAsync_LiveResponseContinuesAfterExtensionIsDisabled()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        IExtensionGenerationLease generationLease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );
        IExtensionInvocationResponse response = await runtime.InvokeEndpointAsync(
            generationLease,
            new ExtensionEndpointInvocation
            {
                ReleaseHash = generation.ReleaseHash,
                Epoch = generation.Epoch,
                EndpointId = "stream",
                Request = JsonDocument.Parse("""{"method":"GET","query":{},"body":null}""").RootElement.Clone(),
            },
            CancellationToken.None
        );
        await generationLease.DisposeAsync();
        await using var streamEnumerator = response.BodyStream!.GetAsyncEnumerator();
        Assert.True(await streamEnumerator.MoveNextAsync());

        Assert.False(
            await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromMilliseconds(20), CancellationToken.None)
        );
        Assert.Equal(ExtensionGenerationState.Draining, Assert.Single(runtime.GetSnapshots()).State);
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            IExtensionEnablementStore enablement =
                scope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
            await enablement.SetAsync(ExtensionId, false);
        }

        Assert.True(await streamEnumerator.MoveNextAsync());
        Assert.Equal("{\"chunk\":2}", Encoding.UTF8.GetString(streamEnumerator.Current.Span));
        Assert.False(await streamEnumerator.MoveNextAsync());
        await streamEnumerator.DisposeAsync();
        await response.DisposeAsync();

        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task InvokeEndpoint_AcceptedLeaseDuringDrain_Continues()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        IExtensionGenerationLease generationLease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );

        Assert.False(
            await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromMilliseconds(20), CancellationToken.None)
        );
        IExtensionInvocationResponse? response = null;
        AgwException? invocationFailure = null;
        try
        {
            response = await runtime.InvokeEndpointAsync(
                generationLease,
                new ExtensionEndpointInvocation
                {
                    ReleaseHash = generation.ReleaseHash,
                    Epoch = generation.Epoch,
                    EndpointId = "echo",
                    Request = JsonDocument
                        .Parse("""{"method":"POST","query":{},"body":{"value":"draining"}}""")
                        .RootElement.Clone(),
                },
                CancellationToken.None
            );
        }
        catch (AgwException exception)
        {
            invocationFailure = exception;
        }
        finally
        {
            if (response != null)
            {
                await response.DisposeAsync();
            }
            await generationLease.DisposeAsync();
        }

        Assert.Null(invocationFailure);
        Assert.Equal("draining", response!.JsonBody!.Value.GetProperty("value").GetString());
        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task UnloadAsync_AfterJsonInvocation_ConfirmsCollectibleGenerationIsReleased()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(
            entryType: "Agw.Extensions.Runtime.Tests.Fixture.TestExtensionWithoutTools"
        );
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        IExtensionGenerationLease generationLease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );
        await using IExtensionInvocationResponse response = await runtime.InvokeEndpointAsync(
            generationLease,
            new ExtensionEndpointInvocation
            {
                ReleaseHash = generation.ReleaseHash,
                Epoch = generation.Epoch,
                EndpointId = "echo",
                Request = JsonDocument
                    .Parse("""{"method":"POST","query":{},"body":{"value":"unload"}}""")
                    .RootElement.Clone(),
            },
            CancellationToken.None
        );
        await response.CompleteAsync();
        await generationLease.DisposeAsync();

        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Empty(runtime.GetSnapshots());
    }

    [Fact]
    public async Task UnloadAsync_AfterExtensionLogging_ConfirmsCollectibleGenerationIsReleased()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        await using IExtensionGenerationLease generationLease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );
        await using IExtensionInvocationResponse response = await runtime.InvokeEndpointAsync(
            generationLease,
            new ExtensionEndpointInvocation
            {
                ReleaseHash = generation.ReleaseHash,
                Epoch = generation.Epoch,
                EndpointId = "log",
                Request = JsonDocument.Parse("""{"method":"GET","query":{},"body":null}""").RootElement.Clone(),
            },
            CancellationToken.None
        );
        await response.CompleteAsync();
        await generationLease.DisposeAsync();

        Assert.NotNull(host.Services.GetRequiredService<ILoggerFactory>());
        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Empty(runtime.GetSnapshots());
    }

    [Fact]
    public async Task UnloadAsync_AfterPrivateEfJsonExceptionAndLogging_CollectsOneHundredGenerations()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(
            extensionActive: true,
            entryType: "Agw.Extensions.Runtime.Tests.Fixture.DatabaseTestExtension"
        );
        ExtensionRuntime runtime = host.Runtime;
        var retainedExceptions = new List<AgwException>();
        var migrationResults = new List<string>();
        var migrationDurations = new List<double>();
        Meter? hostEfMeter = null;
        long hostEfMeasurements = 0;
        using var hostEfMeterListener = new MeterListener();
        hostEfMeterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (hostEfMeter is null && instrument.Meter.Name == "Microsoft.EntityFrameworkCore")
            {
                hostEfMeter = instrument.Meter;
            }

            if (ReferenceEquals(instrument.Meter, hostEfMeter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        hostEfMeterListener.SetMeasurementEventCallback<long>(
            (instrument, _, _, _) =>
            {
                if (ReferenceEquals(instrument.Meter, hostEfMeter))
                {
                    Interlocked.Increment(ref hostEfMeasurements);
                }
            }
        );
        hostEfMeterListener.Start();
        Assert.NotNull(hostEfMeter);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Agw.Extensions.Runtime")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
            {
                if (instrument.Name != "agw.extension.migration.results")
                {
                    return;
                }

                foreach (KeyValuePair<string, object?> tag in tags)
                {
                    if (tag.Key == "result")
                    {
                        migrationResults.Add(tag.Value?.ToString() ?? string.Empty);
                    }
                }
            }
        );
        meterListener.SetMeasurementEventCallback<double>(
            (instrument, measurement, tags, _) =>
            {
                if (instrument.Name == "agw.extension.lifecycle.duration")
                {
                    foreach (KeyValuePair<string, object?> tag in tags)
                    {
                        if (tag.Key == "operation" && tag.Value?.ToString() == "prepare_database")
                        {
                            migrationDurations.Add(measurement);
                            return;
                        }
                    }
                }
            }
        );
        meterListener.Start();

        for (int cycle = 0; cycle < 100; cycle++)
        {
            ExtensionGenerationSnapshot generation = await LoadPreparedAsync(host);
            string loadContextName = AssertDatabaseAssemblyPlacement();
            if (cycle == 0)
            {
                Assert.True(
                    await host.DatabaseTableExistsAsync("zext_019a4c175f007a808b3d2b732f48a100_database_probe")
                );
                Assert.True(
                    await host.DatabaseTableExistsAsync("zext_019a4c175f007a808b3d2b732f48a100___ef_migrations_history")
                );
                await host.InsertDatabaseProbeAsync(1, "alice", "alice");
                await host.InsertDatabaseProbeAsync(2, "bob", "bob");
                await host.PublishExtensionAsync();
            }

            await runtime.ActivateAsync(generation.GenerationId, CancellationToken.None);
            await runtime.ResumeAsync(generation.GenerationId, CancellationToken.None);
            await using IExtensionGenerationLease lease = await runtime.AcquireCurrentAsync(
                generation.GenerationId.ExtensionId,
                CancellationToken.None
            );

            using (UserInfoUtil.Push(CreatePrincipal("alice")))
            {
                JsonElement? databaseResponse = await InvokeEndpointAsync(runtime, lease, "ef-query");
                Assert.Equal(1, databaseResponse!.Value.GetProperty("count").GetInt32());
                JsonElement? capabilityResponse = await InvokeEndpointAsync(runtime, lease, "project-default");
                Assert.Equal("alice", capabilityResponse!.Value.GetProperty("userId").GetString());
                Assert.Equal(JsonValueKind.Null, capabilityResponse.Value.GetProperty("projectId").ValueKind);
            }

            using (UserInfoUtil.Push(CreatePrincipal("bob")))
            {
                JsonElement? databaseResponse = await InvokeEndpointAsync(runtime, lease, "ef-query");
                Assert.Equal(1, databaseResponse!.Value.GetProperty("count").GetInt32());
                JsonElement? capabilityResponse = await InvokeEndpointAsync(runtime, lease, "project-default");
                Assert.Equal("bob", capabilityResponse!.Value.GetProperty("userId").GetString());
                Assert.Equal(JsonValueKind.Null, capabilityResponse.Value.GetProperty("projectId").ValueKind);
            }

            JsonElement? echoResponse = await InvokeEndpointAsync(
                runtime,
                lease,
                "echo",
                """{"method":"POST","query":{},"body":{"value":"private-json"}}"""
            );
            Assert.Equal("private-json", echoResponse!.Value.GetProperty("value").GetString());
            await InvokeEndpointAsync(runtime, lease, "log");

            AgwException extensionException = await Assert.ThrowsAsync<AgwException>(async () =>
                await InvokeEndpointAsync(runtime, lease, "shared-error")
            );
            Assert.Null(extensionException.InnerException);
            retainedExceptions.Add(extensionException);
            await lease.DisposeAsync();

            Assert.True(
                await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None)
            );
            await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Empty(runtime.GetSnapshots());
            Assert.DoesNotContain(AssemblyLoadContext.All, loadContext => loadContext.Name == loadContextName);
        }

        Assert.Equal(100, retainedExceptions.Count);
        Assert.All(retainedExceptions, exception => Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code));
        Assert.Equal(100, migrationResults.Count);
        Assert.All(migrationResults, result => Assert.Equal("success", result));
        Assert.Equal(100, migrationDurations.Count);
        Assert.All(migrationDurations, duration => Assert.True(duration >= 0));

        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            IExtensionEnablementStore enablement =
                scope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
            ExtensionEnablementState state = await enablement.GetAsync(ExtensionId);
            Assert.True(state.Enabled);
        }

        hostEfMeterListener.RecordObservableInstruments();
        Assert.True(Volatile.Read(ref hostEfMeasurements) > 0);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task UnloadAsync_AfterPrivateNpgsqlQuery_CollectibleGenerationIsReleased()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(RequirePostgresConnectionString());
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(
            extensionActive: true,
            databaseProvider: DatabaseProvider.Postgres,
            databaseConnectionString: database.ConnectionString,
            entryType: "Agw.Extensions.Runtime.Tests.Fixture.DatabaseTestExtension"
        );
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadPreparedAsync(host);
        string loadContextName = AssertNpgsqlAssemblyPlacement();

        await host.PublishExtensionAsync();
        await runtime.ActivateAsync(generation.GenerationId, CancellationToken.None);
        await runtime.ResumeAsync(generation.GenerationId, CancellationToken.None);
        await using IExtensionGenerationLease lease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );
        using (UserInfoUtil.Push(CreatePrincipal($"npgsql-{Guid.CreateVersion7():N}")))
        {
            JsonElement? response = await InvokeEndpointAsync(runtime, lease, "ef-query");
            Assert.Equal(0, response!.Value.GetProperty("count").GetInt32());
        }

        await lease.DisposeAsync();
        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Empty(runtime.GetSnapshots());
        Assert.DoesNotContain(AssemblyLoadContext.All, loadContext => loadContext.Name == loadContextName);
    }

    [Fact]
    public async Task InvokeEndpoint_CollectibleJsonTypesWithoutRegisteredContext_AreRejectedAndUnload()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(
            entryType: "Agw.Extensions.Runtime.Tests.Fixture.TestExtensionWithoutJsonContext"
        );
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        await using IExtensionGenerationLease generationLease = await runtime.AcquireCurrentAsync(
            generation.GenerationId.ExtensionId,
            CancellationToken.None
        );

        foreach (string endpointId in new[] { "echo", "echo-list" })
        {
            AgwException exception = await Assert.ThrowsAsync<AgwException>(async () =>
            {
                await runtime.InvokeEndpointAsync(
                    generationLease,
                    new ExtensionEndpointInvocation
                    {
                        ReleaseHash = generation.ReleaseHash,
                        Epoch = generation.Epoch,
                        EndpointId = endpointId,
                        Request = JsonDocument
                            .Parse("""{"method":"POST","query":{},"body":{"value":"unregistered"}}""")
                            .RootElement.Clone(),
                    },
                    CancellationToken.None
                );
            });
            Assert.Equal(ErrorCodes.ExtensionPackageInvalid.Code, exception.Code);
        }

        await generationLease.DisposeAsync();
        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Empty(runtime.GetSnapshots());
    }

    [Fact]
    public async Task AcquireCurrentAsync_SelectedTool_AcquiresAllCompiledExtensionSkills()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        var services = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .AddSingleton<IExtensionGenerationCatalog>(runtime)
            .AddSingleton<IExtensionInvoker>(runtime)
            .AddSingleton<IExtensionRuntimeAvailability>(
                host.Services.GetRequiredService<IExtensionRuntimeAvailability>()
            )
            .AddExtensionCapabilities();
        await using var serviceProvider = services.BuildServiceProvider();
        var extensionToolDefinition = new ExtensionToolDefinition
        {
            ExtensionId = generation.GenerationId.ExtensionId,
            ToolId = "calendar-lookup",
        };
        var selection = new ExtensionCapabilitySelection(
            [new ExtensionToolSelection(generation.GenerationId.ExtensionId, "calendar-lookup")],
            [generation.GenerationId.ExtensionId]
        );
        var toolRegistry = new ToolRegistryService(
            NullLogger<ToolRegistryService>.Instance,
            serviceProvider,
            extensionGenerations: runtime
        );

        var catalogTool = Assert.IsType<ToolInfo>(toolRegistry.GetTool(extensionToolDefinition.GetDefinitionName()));
        Assert.Equal(generation.GenerationId.ExtensionId, catalogTool.ExtensionId);
        Assert.Equal("calendar-lookup", catalogTool.ExtensionToolId);
        Assert.True(catalogTool.AllowInPlanMode);
        Assert.Equal(JsonValueKind.Object, catalogTool.InputSchema!.Value.ValueKind);

        var catalog = serviceProvider.GetRequiredService<IExtensionCapabilityCatalog>();
        await using var capabilities = await catalog.AcquireCurrentAsync(selection, CancellationToken.None);
        Assert.Equal(generation.ReleaseHash, Assert.Single(capabilities.Pins).ReleaseHash);
        Assert.Single(capabilities.Tools);
        var function = Assert.IsAssignableFrom<AIFunction>(Assert.Single(capabilities.SelectTools(selection)));
        var functionResult = Assert.IsType<JsonElement>(
            await function.InvokeAsync(
                new AIFunctionArguments { ["value"] = "weekend" },
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal("calendar:weekend", functionResult.GetString());

        Assert.Collection(
            capabilities.Skills.OrderBy(skill => skill.Frontmatter.Name),
            skill => Assert.Equal("calendar", skill.Frontmatter.Name),
            skill => Assert.Equal("calendar-admin", skill.Frontmatter.Name)
        );
        var skill = Assert.Single(capabilities.Skills, skill => skill.Frontmatter.Name == "calendar");
        Assert.Equal("calendar", skill.Frontmatter.Name);
        Assert.Equal("Calendar instructions", await skill.GetContentAsync(TestContext.Current.CancellationToken));
        await capabilities.DisposeAsync();

        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task ListEnabledSkillExtensionIdsAsync_ExcludesDisabledProviderBeforeDrain()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync();
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        AgwDbContext dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        Assert.Empty(await dbContext.Skills.IgnoreQueryFilters().ToArrayAsync(TestContext.Current.CancellationToken));

        await using var serviceProvider = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .AddSingleton<IExtensionGenerationCatalog>(host.Runtime)
            .AddSingleton<IExtensionInvoker>(host.Runtime)
            .AddSingleton<IExtensionRuntimeAvailability>(
                host.Services.GetRequiredService<IExtensionRuntimeAvailability>()
            )
            .AddExtensionCapabilities()
            .BuildServiceProvider();
        IExtensionCapabilityCatalog catalog = serviceProvider.GetRequiredService<IExtensionCapabilityCatalog>();

        Assert.Equal(
            [generation.GenerationId.ExtensionId],
            await catalog.ListEnabledSkillExtensionIdsAsync(TestContext.Current.CancellationToken)
        );
        await using (
            ExtensionCapabilitySnapshot capabilities = await catalog.AcquireCurrentAsync(
                new ExtensionCapabilitySelection([], [generation.GenerationId.ExtensionId]),
                TestContext.Current.CancellationToken
            )
        )
        {
            Assert.Collection(
                capabilities.Skills.OrderBy(skill => skill.Frontmatter.Name),
                skill => Assert.Equal("calendar", skill.Frontmatter.Name),
                skill => Assert.Equal("calendar-admin", skill.Frontmatter.Name)
            );
        }

        IExtensionEnablementStore enablement = scope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
        await enablement.SetAsync(generation.GenerationId.ExtensionId, false, TestContext.Current.CancellationToken);

        Assert.Empty(await catalog.ListEnabledSkillExtensionIdsAsync(TestContext.Current.CancellationToken));
        await using (
            ExtensionCapabilitySnapshot unavailable = await catalog.AcquireCurrentAsync(
                new ExtensionCapabilitySelection([], [generation.GenerationId.ExtensionId]),
                TestContext.Current.CancellationToken
            )
        )
        {
            Assert.Empty(unavailable.Skills);
            Assert.Empty(unavailable.Pins);
        }
        Assert.Equal(ExtensionGenerationState.Active, Assert.Single(host.Runtime.GetSnapshots()).State);
    }

    [Fact]
    public async Task AcquireForExecutionAsync_RecoveredExtensionIdsUseCurrentReleaseAndPermissions()
    {
        var selection = new ExtensionCapabilitySelection(
            [new ExtensionToolSelection(ExtensionId, "calendar-lookup")],
            [ExtensionId]
        );

        ExtensionExecutionPin previousPin;
        await using (var previousHost = await ExtensionRuntimeTestHost.CreateAsync())
        {
            ExtensionRuntime previousRuntime = previousHost.Runtime;
            ExtensionGenerationSnapshot previousGeneration = await LoadAndActivateAsync(previousHost);
            var previousServices = new ServiceCollection()
                .AddLogging()
                .AddHttpClient()
                .AddSingleton<IExtensionGenerationCatalog>(previousRuntime)
                .AddSingleton<IExtensionInvoker>(previousRuntime)
                .AddSingleton<IExtensionRuntimeAvailability>(
                    previousHost.Services.GetRequiredService<IExtensionRuntimeAvailability>()
                )
                .AddExtensionCapabilities();
            await using var previousServiceProvider = previousServices.BuildServiceProvider();
            IExtensionCapabilityCatalog previousCatalog =
                previousServiceProvider.GetRequiredService<IExtensionCapabilityCatalog>();
            await using (
                ExtensionCapabilitySnapshot previousCapabilities = await previousCatalog.AcquireCurrentAsync(
                    selection,
                    CancellationToken.None
                )
            )
            {
                previousPin = Assert.Single(previousCapabilities.Pins);
                Assert.Equal(previousGeneration.ReleaseHash, previousPin.ReleaseHash);
                Assert.Equal(previousGeneration.Epoch, previousPin.Epoch);
            }

            Assert.True(
                await previousRuntime.DrainAsync(
                    previousGeneration.GenerationId,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None
                )
            );
            await previousRuntime.UnloadAsync(
                previousGeneration.GenerationId,
                TimeSpan.FromSeconds(5),
                CancellationToken.None
            );
        }

        await using var host = await ExtensionRuntimeTestHost.CreateAsync(
            entryType: "Agw.Extensions.Runtime.Tests.Fixture.CurrentTestExtension"
        );
        ExtensionEnablementState enabled;
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            IExtensionEnablementStore enablement =
                scope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
            await enablement.SetAsync(ExtensionId, false);
            enabled = await enablement.SetAsync(ExtensionId, true);
        }

        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot currentGeneration = await LoadAndActivateAsync(
            host,
            host.PackageEntryType,
            enabled.Version
        );
        var services = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .AddSingleton<IExtensionGenerationCatalog>(runtime)
            .AddSingleton<IExtensionInvoker>(runtime)
            .AddSingleton<IExtensionRuntimeAvailability>(
                host.Services.GetRequiredService<IExtensionRuntimeAvailability>()
            )
            .AddExtensionCapabilities();
        await using var serviceProvider = services.BuildServiceProvider();
        IExtensionCapabilityCatalog catalog = serviceProvider.GetRequiredService<IExtensionCapabilityCatalog>();
        var recoveredManifest = new DurableExecutionManifest
        {
            TurnId = Guid.CreateVersion7(),
            UserId = "extension-runtime-test",
            AgentId = Guid.CreateVersion7(),
            AgentType = AgentRuntimeType.Agent,
            Input = new AgwUserInput { Contents = [] },
            Task = new DurableProjectTaskSnapshot
            {
                TaskId = Guid.CreateVersion7(),
                ProjectConversationId = Guid.CreateVersion7(),
                ProjectId = Guid.CreateVersion7(),
                ContextId = "extension-recovery",
            },
            Settings = new DurableExecutionSettings { EnvironmentVariables = [], Resume = false },
            ExtensionIds = [previousPin.ExtensionId],
        };
        await host.AddActiveExecutionAsync(recoveredManifest.ExtensionIds);
        await host.AddUnreadableActiveExecutionAsync();
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            IExtensionEnablementStore enablement =
                scope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
            await enablement.SetAsync(ExtensionId, false);
        }
        Assert.True(
            await runtime.DrainAsync(currentGeneration.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None)
        );
        Assert.Equal(ExtensionGenerationState.Draining, Assert.Single(runtime.GetSnapshots()).State);

        await using var recovered = await catalog.AcquireForExecutionAsync(
            recoveredManifest.ExtensionIds,
            selection,
            TestContext.Current.CancellationToken
        );

        ExtensionExecutionPin currentPin = Assert.Single(recovered.Pins);
        Assert.NotEqual(previousPin.ReleaseHash, currentPin.ReleaseHash);
        Assert.NotEqual(previousPin.Epoch, currentPin.Epoch);
        Assert.Equal(currentGeneration.ReleaseHash, currentPin.ReleaseHash);
        Assert.Equal(currentGeneration.Epoch, currentPin.Epoch);
        AIFunction currentTool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(recovered.SelectTools(selection)));
        AgwToolMetadata currentMetadata = Assert.IsType<AgwToolMetadata>(
            AgwToolMetadataBinding.GetMetadata(currentTool)
        );
        Assert.Equal(AgwToolPermission.Write, currentMetadata.RequiredPermission);
        Assert.False(currentMetadata.AllowInPlanMode);
        AgentSkill currentSkill = Assert.Single(recovered.Skills);
        Assert.Equal(
            "Current calendar instructions",
            await currentSkill.GetContentAsync(TestContext.Current.CancellationToken)
        );

        await recovered.DisposeAsync();
        Assert.True(
            await runtime.DrainAsync(currentGeneration.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None)
        );
        await runtime.UnloadAsync(currentGeneration.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task ComposeAsync_RecoveredUnavailableExtensionSkill_UsesLoadedDrainingGeneration()
    {
        await using var host = await ExtensionRuntimeTestHost.CreateAsync(
            entryType: "Agw.Extensions.Runtime.Tests.Fixture.CurrentTestExtension"
        );
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await LoadAndActivateAsync(host);
        using var userScope = UserInfoUtil.Push(CreatePrincipal("extension-runtime-test"));
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        AgwDbContext dbContext = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
        var userInfo = new UserInfoService();
        var agentId = Guid.CreateVersion7();
        dbContext.Agents.Add(
            new Agent
            {
                Id = agentId,
                Name = "extension-recovery-agent",
                Type = AgentType.System,
                CreateBy = "extension-runtime-test",
            }
        );
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await dbContext.Skills.IgnoreQueryFilters().ToArrayAsync(TestContext.Current.CancellationToken));
        await host.AddActiveExecutionAsync([ExtensionId]);
        IExtensionEnablementStore enablement = scope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
        await enablement.SetAsync(ExtensionId, false, TestContext.Current.CancellationToken);
        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Equal(ExtensionGenerationState.Draining, Assert.Single(runtime.GetSnapshots()).State);

        var definitionRepository = new AgentDefinitionRepository(dbContext, userInfo);
        var modelProviderReferences = new ModelProviderReferenceFacade(dbContext, userInfo);
        var connectionReferences = new ConnectionReferenceFacade(dbContext, userInfo);
        var skillReferences = new SkillReferenceFacade(dbContext, userInfo);
        var agentAppService = new AgentAppService(
            dbContext,
            new AgentDefinitionDomainService(definitionRepository, modelProviderReferences),
            new AgentResourceBindingDomainService(definitionRepository, skillReferences, connectionReferences),
            modelProviderReferences,
            skillReferences,
            userInfo,
            new AgentDeletionCoordinator(dbContext, Agw.Shared.Coordination.InMemoryApplicationLock.Shared),
            Agw.Shared.Coordination.InMemoryApplicationLock.Shared
        );
        var agent = await agentAppService.GetAgentForCurrentUserAsync(agentId);
        Assert.NotNull(agent);
        Assert.Empty(agent.AgentSkillRelations);

        await using var integrationServices = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .AddSingleton<IIntegrationsDbContext>(dbContext)
            .AddSingleton<ICurrentUser>(userInfo)
            .AddSingleton<IUserInfoService>(userInfo)
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddIntegrations(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        using var toolServices = new ServiceCollection().AddLogging().AddHttpClient().BuildServiceProvider();
        await using var extensionServices = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .AddSingleton<IExtensionGenerationCatalog>(runtime)
            .AddSingleton<IExtensionInvoker>(runtime)
            .AddSingleton<IExtensionRuntimeAvailability>(
                host.Services.GetRequiredService<IExtensionRuntimeAvailability>()
            )
            .AddExtensionCapabilities()
            .BuildServiceProvider();
        var composer = new AgentCapabilityComposer(
            agentAppService,
            new ToolRegistryService(
                NullLogger<ToolRegistryService>.Instance,
                toolServices,
                extensionGenerations: runtime
            ),
            integrationServices.GetRequiredService<IConnectionCapabilityResolver>(),
            integrationServices.GetRequiredService<IMcpToolMaterializer>(),
            new ToolBlockRegistry([]),
            NullLogger<AgentCapabilityComposer>.Instance,
            extensionCapabilityCatalog: extensionServices.GetRequiredService<IExtensionCapabilityCatalog>()
        );
        agent.Tools =
        [
            new ToolValue
            {
                Definition = new ExtensionToolDefinition { ExtensionId = ExtensionId, ToolId = "calendar-lookup" },
            },
        ];
        var project = new Agw.Shared.Data.Entities.Projects.Project { Id = Guid.CreateVersion7() };

        await using (
            AgentCapabilityComposition composition = await InvokeComposeAsync(
                composer,
                agent,
                project,
                [ExtensionId],
                TestContext.Current.CancellationToken
            )
        )
        {
            AgwToolMetadata toolMetadata = Assert.IsType<AgwToolMetadata>(
                AgwToolMetadataBinding.GetMetadata(Assert.Single(composition.Tools))
            );
            Assert.Equal(AgwToolPermission.Write, toolMetadata.RequiredPermission);
            Assert.False(toolMetadata.AllowInPlanMode);
            AgentSkill currentSkill = Assert.Single(composition.ExtensionSkills);
            Assert.Equal(
                "Current calendar instructions",
                await currentSkill.GetContentAsync(TestContext.Current.CancellationToken)
            );
        }

        Assert.True(await runtime.DrainAsync(generation.GenerationId, TimeSpan.FromSeconds(1), CancellationToken.None));
        await runtime.UnloadAsync(generation.GenerationId, TimeSpan.FromSeconds(5), CancellationToken.None);

        AgwException unavailable = await Assert.ThrowsAsync<AgwException>(() =>
            InvokeComposeAsync(composer, agent, project, [ExtensionId], TestContext.Current.CancellationToken)
        );
        Assert.Equal(ErrorCodes.ExtensionUnavailable.Code, unavailable.Code);
    }

    private static Task<AgentCapabilityComposition> InvokeComposeAsync(
        AgentCapabilityComposer composer,
        Agent agent,
        Agw.Shared.Data.Entities.Projects.Project project,
        IReadOnlyList<Guid> extensionIds,
        CancellationToken cancellationToken
    )
    {
        MethodInfo method =
            typeof(AgentCapabilityComposer).GetMethod("ComposeAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find AgentCapabilityComposer.ComposeAsync.");
        return Assert.IsType<Task<AgentCapabilityComposition>>(
            method.Invoke(
                composer,
                [
                    agent,
                    project,
                    new Dictionary<string, string>(),
                    cancellationToken,
                    false,
                    "execute",
                    null,
                    Guid.Empty,
                    false,
                    null,
                    null,
                    extensionIds,
                    BackgroundToolPolicy.Default,
                ]
            )
        );
    }

    private static async Task<ExtensionGenerationSnapshot> LoadAndActivateAsync(
        ExtensionRuntimeTestHost host,
        string? entryType = null,
        long epoch = 1
    )
    {
        ExtensionGenerationSnapshot generation = await LoadPreparedAsync(host, entryType, epoch);
        ExtensionRuntime runtime = host.Runtime;
        await runtime.ActivateAsync(generation.GenerationId, CancellationToken.None);
        await runtime.ResumeAsync(generation.GenerationId, CancellationToken.None);
        return generation;
    }

    private static async Task<ExtensionGenerationSnapshot> LoadPreparedAsync(
        ExtensionRuntimeTestHost host,
        string? entryType = null,
        long epoch = 1
    )
    {
        ExtensionRuntime runtime = host.Runtime;
        ExtensionGenerationSnapshot generation = await runtime.LoadAsync(
            CreateLoadRequest(host, entryType, epoch),
            CancellationToken.None
        );
        await runtime.PrepareDatabaseAsync(generation.GenerationId, CancellationToken.None);
        return generation;
    }

    private static ExtensionRuntimeLoadRequest CreateLoadRequest(
        ExtensionRuntimeTestHost host,
        string? entryType = null,
        long epoch = 1
    )
    {
        entryType ??= host.PackageEntryType;
        if (!string.Equals(entryType, host.PackageEntryType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The requested entry type must match the local package manifest.");
        }

        return new ExtensionRuntimeLoadRequest
        {
            ExtensionId = ExtensionId,
            Name = "runtime-test",
            Version = "1.0.0",
            ReleaseHash = host.PackageReleaseHash,
            Epoch = epoch,
            DataDirectory = host.DataDirectory,
            PackageDirectory = host.PackageDirectory,
            EntryAssemblyPath = "server/Agw.Extensions.Runtime.TestExtension.dll",
            EntryType = entryType,
        };
    }

    private static async Task<JsonElement?> InvokeEndpointAsync(
        IExtensionInvoker runtime,
        IExtensionGenerationLease lease,
        string endpointId,
        string requestJson = """{"method":"GET","query":{},"body":null}"""
    )
    {
        using JsonDocument request = JsonDocument.Parse(requestJson);
        await using IExtensionInvocationResponse response = await runtime.InvokeEndpointAsync(
            lease,
            new ExtensionEndpointInvocation
            {
                ReleaseHash = lease.Pin.ReleaseHash,
                Epoch = lease.Pin.Epoch,
                EndpointId = endpointId,
                Request = request.RootElement.Clone(),
            },
            CancellationToken.None
        );
        await response.CompleteAsync();
        return response.JsonBody?.Clone();
    }

    private static ClaimsPrincipal CreatePrincipal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "runtime-test"));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string AssertDatabaseAssemblyPlacement()
    {
        AssemblyLoadContext extensionLoadContext = Assert.Single(
            AssemblyLoadContext.All,
            loadContext =>
                loadContext.IsCollectible
                && loadContext.Name?.StartsWith("Agw.Extension.", StringComparison.Ordinal) == true
        );
        Assert.Contains(
            extensionLoadContext.Assemblies,
            assembly => assembly.GetName().Name == "Microsoft.EntityFrameworkCore"
        );
        Assert.Contains(
            extensionLoadContext.Assemblies,
            assembly => assembly.GetName().Name == "Microsoft.EntityFrameworkCore.Sqlite"
        );
        Assert.DoesNotContain(
            extensionLoadContext.Assemblies,
            assembly => assembly.GetName().Name == "Microsoft.Data.Sqlite"
        );
        Assert.DoesNotContain(
            extensionLoadContext.Assemblies,
            assembly => assembly.GetName().Name == "SQLitePCLRaw.core"
        );
        Assert.Contains(
            AssemblyLoadContext.Default.Assemblies,
            assembly => assembly.GetName().Name == "Microsoft.Data.Sqlite"
        );
        Assert.Contains(
            AssemblyLoadContext.Default.Assemblies,
            assembly => assembly.GetName().Name == "SQLitePCLRaw.core"
        );
        return extensionLoadContext.Name!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string AssertNpgsqlAssemblyPlacement()
    {
        AssemblyLoadContext extensionLoadContext = Assert.Single(
            AssemblyLoadContext.All,
            loadContext =>
                loadContext.IsCollectible
                && loadContext.Name?.StartsWith("Agw.Extension.", StringComparison.Ordinal) == true
        );
        Assert.Contains(
            extensionLoadContext.Assemblies,
            assembly => assembly.GetName().Name == "Microsoft.EntityFrameworkCore"
        );
        Assert.Contains(
            extensionLoadContext.Assemblies,
            assembly => assembly.GetName().Name == "Npgsql.EntityFrameworkCore.PostgreSQL"
        );
        Assert.Contains(extensionLoadContext.Assemblies, assembly => assembly.GetName().Name == "Npgsql");
        return extensionLoadContext.Name!;
    }

    private static string RequirePostgresConnectionString()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
        {
            directory = directory.Parent;
        }

        string repositoryRoot =
            directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the Agw solution root.");
        string configPath = Path.Combine(repositoryRoot, ".scratch", "postgres-tests.json");
        if (!File.Exists(configPath))
        {
            Assert.Skip("PostgreSQL test configuration is not present.");
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath));
        string? connectionString = document.RootElement.GetProperty("ConnectionString").GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        return connectionString;
    }

    private sealed class PostgreSqlTestDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private PostgreSqlTestDatabase(string adminConnectionString, string databaseName, string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<PostgreSqlTestDatabase> CreateAsync(string adminConnectionString)
        {
            string databaseName = $"agw_runtime_{Guid.CreateVersion7():N}";
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(databaseName)}";
            await command.ExecuteNonQueryAsync();

            var connectionStringBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
            {
                Database = databaseName,
            };
            return new PostgreSqlTestDatabase(
                adminConnectionString,
                databaseName,
                connectionStringBuilder.ConnectionString
            );
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"DROP DATABASE IF EXISTS {new NpgsqlCommandBuilder().QuoteIdentifier(_databaseName)}";
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class ExtensionRuntimeTestHost : IAsyncDisposable
    {
        private readonly DbConnection _connection;
        private readonly ServiceProvider _services;
        private readonly AgwDataPaths _paths;
        private readonly DatabaseProvider _databaseProvider;
        private readonly string _packageDirectory;
        private readonly string _packageReleaseHash;
        private readonly string _packageEntryType;

        private ExtensionRuntimeTestHost(
            DbConnection connection,
            ServiceProvider services,
            AgwDataPaths paths,
            DatabaseProvider databaseProvider,
            string packageDirectory,
            string packageReleaseHash,
            string packageEntryType
        )
        {
            _connection = connection;
            _services = services;
            _paths = paths;
            _databaseProvider = databaseProvider;
            _packageDirectory = packageDirectory;
            _packageReleaseHash = packageReleaseHash;
            _packageEntryType = packageEntryType;
        }

        public ExtensionRuntime Runtime => _services.GetRequiredService<ExtensionRuntime>();

        public IServiceProvider Services => _services;

        public string DataDirectory => Path.Combine(_paths.Root, "extensions", "data", ExtensionId.ToString("N"));

        public string PackageDirectory => _packageDirectory;

        public string PackageReleaseHash => _packageReleaseHash;

        public string PackageEntryType => _packageEntryType;

        public async Task PublishExtensionAsync()
        {
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            IExtensionEnablementStore enablement =
                scope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
            await enablement.SetAsync(ExtensionId, true);
        }

        public async Task AddActiveExecutionAsync(IReadOnlyList<Guid> extensionIds)
        {
            Guid turnId = Guid.CreateVersion7();
            Guid projectId = Guid.CreateVersion7();
            Guid conversationId = Guid.CreateVersion7();
            const string userId = "extension-runtime-test";
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
                    ContextId = "extension-recovery",
                },
                Settings = new DurableExecutionSettings { EnvironmentVariables = [], Resume = false },
                WorkspaceSnapshot = ProjectWorkspacePaths.CreateSnapshot(projectId, null),
                ExtensionIds = extensionIds,
            };

            await using AsyncServiceScope scope = _services.CreateAsyncScope();
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
            await db.SaveChangesAsync();
        }

        public async Task AddUnreadableActiveExecutionAsync()
        {
            const string userId = "extension-runtime-unreadable-manifest";
            await using AsyncServiceScope scope = _services.CreateAsyncScope();
            AgwDbContext db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
            db.DurableExecutions.Add(
                new DurableExecutionRecord
                {
                    Id = Guid.CreateVersion7(),
                    UserId = userId,
                    ProjectId = Guid.CreateVersion7(),
                    ProjectConversationId = Guid.CreateVersion7(),
                    ScopeBackfilled = true,
                    ManifestJson = "invalid-json",
                    Status = DurableExecutionStatus.Queued,
                    StateChangedAt = TimeProvider.System.GetUtcNow(),
                    StateVersion = Guid.CreateVersion7(),
                    CreateBy = userId,
                }
            );
            await db.SaveChangesAsync();
        }

        public async Task<bool> DatabaseTableExistsAsync(string tableName)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                _databaseProvider == DatabaseProvider.Sqlite
                    ? "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name"
                    : "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @name";
            AddParameter(command, "@name", tableName);
            object? result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result) > 0;
        }

        public async Task InsertDatabaseProbeAsync(int id, string value, string owner)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO zext_019a4c175f007a808b3d2b732f48a100_database_probe
                    (id, value, create_by, create_time, update_by, update_time)
                VALUES (@id, @value, @owner, @create_time, NULL, NULL)
                """;
            AddParameter(command, "@id", id);
            AddParameter(command, "@value", value);
            AddParameter(command, "@owner", owner);
            AddParameter(
                command,
                "@create_time",
                _databaseProvider == DatabaseProvider.Sqlite
                    ? "2026-01-01T00:00:00.0000000+00:00"
                    : new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            );
            await command.ExecuteNonQueryAsync();
        }

        private static void AddParameter(DbCommand command, string name, object value)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        public static async Task<ExtensionRuntimeTestHost> CreateAsync(
            string hostRole = "Standalone",
            bool extensionActive = true,
            DatabaseProvider databaseProvider = DatabaseProvider.Sqlite,
            string? databaseConnectionString = null,
            string entryType = "Agw.Extensions.Runtime.Tests.Fixture.TestExtension",
            TimeSpan? unloadTimeout = null
        )
        {
            AgwDataPaths paths = AgwDataPaths.Resolve(
                Path.Combine(AppContext.BaseDirectory, $"runtime-test-{Guid.CreateVersion7():N}"),
                AppContext.BaseDirectory
            );
            Directory.CreateDirectory(paths.Root);
            string sourceDirectory = Path.Combine(paths.Root, "source");
            string packageDirectory = Path.Combine(sourceDirectory, "runtime-test");
            string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "ExtensionFixture");
            Dictionary<string, byte[]> files = Directory
                .GetFiles(fixtureDirectory, "*", SearchOption.AllDirectories)
                .ToDictionary(
                    path => "server/" + Path.GetRelativePath(fixtureDirectory, path).Replace('\\', '/'),
                    File.ReadAllBytes,
                    StringComparer.Ordinal
                );
            foreach (var (relativePath, contents) in files)
            {
                string path = Path.Combine(packageDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, contents);
            }

            string manifestJson = JsonSerializer.Serialize(
                new
                {
                    manifestVersion = 2,
                    id = ExtensionId.ToString("D"),
                    name = "runtime-test",
                    displayName = "Runtime Test",
                    version = "1.0.0",
                    targets = 1,
                    server = new
                    {
                        apiVersion = 1,
                        entryAssembly = "server/Agw.Extensions.Runtime.TestExtension.dll",
                        entryType,
                    },
                    files = files.ToDictionary(
                        pair => pair.Key,
                        pair => Convert.ToHexStringLower(SHA256.HashData(pair.Value)),
                        StringComparer.Ordinal
                    ),
                }
            );
            await File.WriteAllTextAsync(Path.Combine(packageDirectory, "manifest.json"), manifestJson);
            string connectionString =
                databaseConnectionString ?? $"Data Source={Path.Combine(paths.Root, "runtime-test.db")}";
            DbConnection connection =
                databaseProvider == DatabaseProvider.Sqlite
                    ? new SqliteConnection(connectionString)
                    : new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var extensionConfigurationValues = new Dictionary<string, string?>
            {
                ["Extensions:Directory"] = sourceDirectory,
            };
            if (unloadTimeout is { } timeout)
            {
                extensionConfigurationValues["Extensions:UnloadTimeout"] = timeout.ToString("c");
            }
            IConfiguration extensionConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(extensionConfigurationValues)
                .Build();
            ServiceCollection serviceCollection = new ServiceCollection();
            serviceCollection
                .AddDataProtection()
                .PersistKeysToFileSystem(
                    new DirectoryInfo(Path.Combine(paths.Root, "extensions", "data", ExtensionId.ToString("N"), "keys"))
                );
            serviceCollection.AddSingleton<IApplicationLock, InMemoryApplicationLock>();
            serviceCollection
                .AddLogging(builder =>
                    builder
                        .AddConsole()
                        .SetMinimumLevel(LogLevel.Error)
                        .AddFilter("Agw.Extensions.Infrastructure.Runtime.ExtensionRuntime", LogLevel.Information)
                )
                .AddSingleton(paths)
                .AddSingleton<TimeProvider>(TimeProvider.System)
                .AddSingleton<IHostEnvironment>(new TestHostEnvironment(AppContext.BaseDirectory))
                .Configure<DatabaseSettings>(settings =>
                {
                    settings.Provider = databaseProvider;
                    settings.ConnectionString = connectionString;
                })
                .AddDbContext<AgwDbContext>(builder =>
                {
                    if (databaseProvider == DatabaseProvider.Sqlite)
                    {
                        builder.UseSqlite(connectionString);
                    }
                    else
                    {
                        builder.UseNpgsql(connectionString);
                    }

                    builder.UseSnakeCaseNamingConvention();
                })
                .AddScoped<IProjectsDbContext>(provider => provider.GetRequiredService<AgwDbContext>())
                .AddScoped<ISettingsDbContext>(provider => provider.GetRequiredService<AgwDbContext>())
                .AddScoped<ISettingsPersistence, EfSettingsPersistence>()
                .AddScoped<IExtensionExecutionUsage, DurableExecutionScopeMaintenance>()
                .AddScoped<ICurrentUser, UserInfoService>()
                .AddScoped<IUserInfoService, UserInfoService>()
                .AddSettings()
                .AddSingleton<IExtensionDeploymentIdentity, ExtensionDeploymentIdentity>()
                .AddExtensionInfrastructure()
                .AddExtensions(extensionConfiguration, hostRole)
                .AddProjects(new ConfigurationBuilder().Build())
                .AddExtensionHostCapability<IProjectDefaultResolver>()
                .AddExtensionHostCapability<IExtensionCallerAccessor>();
            ServiceProvider services = serviceCollection.BuildServiceProvider();
            ExtensionPackageCatalog packageCatalog = services.GetRequiredService<ExtensionPackageCatalog>();
            await packageCatalog.InitializeAsync(CancellationToken.None);

            await using (AsyncServiceScope scope = services.CreateAsyncScope())
            {
                AgwDbContext db = scope.ServiceProvider.GetRequiredService<AgwDbContext>();
                await db.Database.EnsureCreatedAsync();
                await scope
                    .ServiceProvider.GetRequiredService<ISettingsStore>()
                    .TryCreateGlobalAsync(
                        ExtensionDeploymentSettings.Key,
                        new ExtensionDeploymentSettings { ServerId = Guid.CreateVersion7().ToString() },
                        TestContext.Current.CancellationToken
                    );
                if (extensionActive)
                {
                    IExtensionEnablementStore enablement =
                        scope.ServiceProvider.GetRequiredService<IExtensionEnablementStore>();
                    await enablement.SetAsync(ExtensionId, true);
                }
            }

            ExtensionLocalPackage package = packageCatalog.GetValidPackage(ExtensionId);

            return new ExtensionRuntimeTestHost(
                connection,
                services,
                paths,
                databaseProvider,
                packageDirectory,
                package.ReleaseHash!,
                entryType
            );
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
            if (Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            if (Directory.Exists(_paths.Root))
            {
                Directory.Delete(_paths.Root, recursive: true);
            }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Agw.Extensions.Runtime.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
