using System.Text.Json;

using Agw.Infrastructure.Configuration;
using Agw.Setup.Contracts;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agw.Setup.Services;

public class JsonInitializationStateStore : IInitializationStateStore
{
    private readonly string _settingsPath;
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private InitializationSnapshot _current;

    public JsonInitializationStateStore(
        IHostEnvironment hostEnvironment,
        IOptionsMonitor<SystemInitializationSettings> initializationOptions)
    {
        _settingsPath = Path.Combine(hostEnvironment.ContentRootPath, "appsettings.setup.json");
        var settings = initializationOptions.CurrentValue;
        _current = new InitializationSnapshot(settings.IsInitialized, settings.ApiKey);
        initializationOptions.OnChange(updated =>
        {
            lock (_sync)
            {
                _current = new InitializationSnapshot(updated.IsInitialized, updated.ApiKey);
            }
        });
    }

    public InitializationSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _current;
        }
    }

    public async Task PersistAsync(SetupRequest request, CancellationToken cancellationToken = default)
    {
        var payload = new SetupSettingsPayload
        {
            Database = new DatabaseSettings
            {
                Provider = request.Provider,
                ConnectionString = request.ConnectionString
            },
            SystemInitialization = new SystemInitializationSettings
            {
                IsInitialized = true,
                ApiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim()
            }
        };

        var settingsDirectory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(settingsDirectory))
        {
            Directory.CreateDirectory(settingsDirectory);
        }

        var tempPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             options: FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, payload, _serializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(_settingsPath))
            {
                File.Replace(tempPath, _settingsPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _settingsPath);
            }
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }

        lock (_sync)
        {
            _current = new InitializationSnapshot(true, payload.SystemInitialization.ApiKey);
        }
    }

    private sealed class SetupSettingsPayload
    {
        public DatabaseSettings Database { get; init; } = new();

        public SystemInitializationSettings SystemInitialization { get; init; } = new();
    }
}
