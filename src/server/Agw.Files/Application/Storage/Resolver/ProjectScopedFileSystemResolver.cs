using System.Collections.Concurrent;

using Agw.Files.Application.Storage.Local;
using Agw.Files.Application.Storage.Sftp;
using Agw.Shared.Contracts.Storage;
using Agw.Shared.Contracts.Projects;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Application.Storage.Resolver;

public sealed class ProjectScopedFileSystemResolver : IAgwFileSystemResolver, IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LocalFileSystemFactory _localFactory;
    private readonly SftpFileSystemFactory _sftpFactory;
    private readonly ILogger<ProjectScopedFileSystemResolver> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, CachedEntry> _cache = new();

    public ProjectScopedFileSystemResolver(
        IServiceScopeFactory scopeFactory,
        LocalFileSystemFactory localFactory,
        SftpFileSystemFactory sftpFactory,
        ILogger<ProjectScopedFileSystemResolver> logger,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _localFactory = localFactory;
        _sftpFactory = sftpFactory;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public async Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct)
    {
        if (projectId == Guid.Empty)
        {
            return CreateFallbackLocal();
        }

        if (_cache.TryGetValue(projectId, out var cached))
        {
            return cached.FileSystem;
        }

        var fs = await CreateForProjectAsync(projectId, ct);

        _cache[projectId] = new CachedEntry(fs, _timeProvider.GetUtcNow());
        return fs;
    }

    private async Task<IAgwFileSystem> CreateForProjectAsync(Guid projectId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var projectAppService = scope.ServiceProvider.GetRequiredService<IProjectAppService>();

        var project = await projectAppService.GetAsync(projectId);
        if (project == null)
        {
            _logger.LogWarning("Project {ProjectId} not found, falling back to default local file system.", projectId);
            return CreateFallbackLocal();
        }

        // Try to parse fileStorage config from ExtraSetting
        if (!string.IsNullOrWhiteSpace(project.ExtraSetting))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(project.ExtraSetting);
                if (doc.RootElement.TryGetProperty("fileStorage", out var fsElement))
                {
                    return CreateFromConfig(fsElement, project.Workspace);
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse ExtraSetting for project {ProjectId}", projectId);
            }
        }

        // Fallback: use Project.Workspace as Local root
        var rootPath = !string.IsNullOrWhiteSpace(project.Workspace)
            ? project.Workspace
            : $"~/.agw/{project.Name}";
        
        _logger.LogInformation("Project {ProjectId} has no fileStorage config, using local workspace: {Path}", projectId, rootPath);
        return _localFactory.Create(rootPath);
    }

    private IAgwFileSystem CreateFromConfig(System.Text.Json.JsonElement config, string? fallbackWorkspace)
    {
        if (!config.TryGetProperty("type", out var typeProp))
        {
            throw new AgwException(ErrorCodes.FileStorageConfigInvalid, "fileStorage config is missing 'type' field.");
        }

        var type = typeProp.GetString();
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new AgwException(ErrorCodes.FileStorageConfigInvalid, "fileStorage 'type' must be a non-empty string.");
        }

        return type.ToLowerInvariant() switch
        {
            "local" => CreateLocalFromConfig(config),
            "sftp" => CreateSftpFromConfig(config),
            _ => throw new AgwException(ErrorCodes.FileStorageBackendNotSupported,
                $"File storage backend '{type}' is not supported.")
        };
    }

    private IAgwFileSystem CreateLocalFromConfig(System.Text.Json.JsonElement config)
    {
        var localOptions = new LocalFileStorageOptions();

        if (config.TryGetProperty("local", out var localConfig))
        {
            if (localConfig.TryGetProperty("rootPath", out var rp))
            {
                localOptions.RootPath = rp.GetString() ?? "";
            }
        }

        if (string.IsNullOrWhiteSpace(localOptions.RootPath))
        {
            throw new AgwException(ErrorCodes.FileStorageConfigInvalid, "Local file storage requires 'rootPath'.");
        }

        return _localFactory.Create(localOptions);
    }

    private IAgwFileSystem CreateSftpFromConfig(System.Text.Json.JsonElement config)
    {
        var sftpOptions = new SftpFileStorageOptions();

        if (!config.TryGetProperty("sftp", out var sftpConfig))
        {
            throw new AgwException(ErrorCodes.FileStorageConfigInvalid, "SFTP file storage requires 'sftp' config section.");
        }

        if (sftpConfig.TryGetProperty("host", out var host))
            sftpOptions.Host = host.GetString() ?? "";
        if (sftpConfig.TryGetProperty("port", out var port))
            sftpOptions.Port = port.GetInt32();
        if (sftpConfig.TryGetProperty("username", out var username))
            sftpOptions.Username = username.GetString() ?? "";
        if (sftpConfig.TryGetProperty("authType", out var authType))
            sftpOptions.AuthType = authType.GetString() ?? "password";
        if (sftpConfig.TryGetProperty("password", out var password))
            sftpOptions.Password = password.GetString();
        if (sftpConfig.TryGetProperty("privateKeyPath", out var privateKeyPath))
            sftpOptions.PrivateKeyPath = privateKeyPath.GetString();
        if (sftpConfig.TryGetProperty("passphrase", out var passphrase))
            sftpOptions.Passphrase = passphrase.GetString();
        if (sftpConfig.TryGetProperty("rootPath", out var rootPath))
            sftpOptions.RootPath = rootPath.GetString() ?? "";

        return _sftpFactory.Create(sftpOptions);
    }

    private IAgwFileSystem CreateFallbackLocal()
    {
        var tempWorkspace = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agw", "temp");

        Directory.CreateDirectory(tempWorkspace);
        return _localFactory.Create(tempWorkspace);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _cache.Values)
        {
            if (entry.FileSystem is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
        }

        _cache.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed record CachedEntry(IAgwFileSystem FileSystem, DateTimeOffset CreatedAt);
}
