using System.Net;
using System.Text.Json;
using Agw.Files.Abstracts;
using Agw.Files.Api;
using Agw.Files.Application.Files;
using Agw.Files.Infrastructure.Git;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Exceptions;
using Agw.Shared.Results;
using Bens.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

/// <summary>
/// <para>Files 端点的失败路径经过共享的 AgwApiExceptionMiddleware，返回 Bens.Results 信封与共享错误码。</para>
/// <para>Files endpoint failures flow through the shared AgwApiExceptionMiddleware and return Bens.Results envelopes with shared error codes.</para>
/// </summary>
public sealed class FilesEndpointErrorEnvelopeTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("agw-files-envelope-").FullName;
    private readonly Guid _projectId = Guid.NewGuid();

    [Fact]
    public async Task Read_PathOutsideRoot_ReturnsForbiddenEnvelopeWithSharedCode()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            $"/api/files/read?projectId={_projectId}&path=../outside.txt",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var payload = await ReadJsonAsync(response);
        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, payload.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Read_UnavailableProjectDirectory_ReturnsNotFoundEnvelopeWithSharedCode()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        Directory.Delete(_workspace, recursive: true);

        using var response = await client.GetAsync(
            $"/api/files/read?projectId={_projectId}&path=README.md",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var payload = await ReadJsonAsync(response);
        Assert.Equal(ErrorCodes.ResourceNotFound.Code, payload.GetProperty("code").GetInt32());
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddApiResult();
        builder.Services.AddSingleton<IProjectFileSystemConfigurationProvider>(
            new WorkspaceConfigurationProvider(new ProjectFileSystemConfiguration("Project", _workspace, "owner", []))
        );
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAgwFileSystemResolver>(provider => new ProjectScopedFileSystemResolver(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProjectScopedFileSystemResolver>.Instance,
            TimeProvider.System
        ));
        builder.Services.AddSingleton<IGitCommandService>(
            new GitCommandService(NullLogger<GitCommandService>.Instance)
        );
        builder.Services.AddSingleton<FileAppService>();
        builder.Services.AddControllers().AddApplicationPart(typeof(FilesController).Assembly);
        var app = builder.Build();
        app.UseMiddleware<AgwApiExceptionMiddleware>();
        app.MapControllers();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: TestContext.Current.CancellationToken
        );
        return document.RootElement.Clone();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    private sealed class WorkspaceConfigurationProvider : IProjectFileSystemConfigurationProvider
    {
        private readonly ProjectFileSystemConfiguration _configuration;

        public WorkspaceConfigurationProvider(ProjectFileSystemConfiguration configuration)
        {
            _configuration = configuration;
        }

        public Task<ProjectFileSystemConfiguration?> GetAsync(
            Guid projectId,
            CancellationToken cancellationToken = default
        ) => Task.FromResult<ProjectFileSystemConfiguration?>(_configuration);

        public Task<string?> GetOwnerUserIdAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(_configuration.OwnerUserId);
    }
}
