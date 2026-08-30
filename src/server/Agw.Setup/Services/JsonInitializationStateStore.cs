using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Auth.Contracts;
using Agw.Setup.Contracts;
using Agw.Shared.Configuration;
using Agw.Shared.Runtime;

namespace Agw.Setup.Services;

public sealed class JsonInitializationStateStore
    : IInitializationStateStore,
        IAuthenticationStateStore,
        IServerInitializationState
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private readonly AgwDataPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = CreateSerializerOptions();
    private volatile ServerState _state;

    public JsonInitializationStateStore(AgwDataPaths paths)
        : this(paths, TimeProvider.System) { }

    public JsonInitializationStateStore(AgwDataPaths paths, TimeProvider timeProvider)
    {
        _paths = paths;
        _timeProvider = timeProvider;
        _state = Load(paths.StateFile);
    }

    public AuthenticationSnapshot GetAuthenticationSnapshot()
    {
        var state = _state;
        return new AuthenticationSnapshot(state.PasswordHash, state.SessionVersion);
    }

    public bool IsInitialized
    {
        get { return _state.IsInitialized; }
    }

    public bool HasLegacyApiTokenSection
    {
        get { return _state.Tokens != null; }
    }

    public DatabaseProvider DatabaseProvider
    {
        get { return _state.Database.Provider; }
    }

    public string DatabaseConnectionString
    {
        get { return _state.Database.ConnectionString; }
    }

    public async Task PersistAsync(
        SetupConfiguration configuration,
        string passwordHash,
        CancellationToken cancellationToken = default
    )
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var stateFileLock = await AcquireStateFileLockAsync(cancellationToken);
            RefreshFromDisk();
            var nextState = new ServerState
            {
                SchemaVersion = 2,
                IsInitialized = true,
                Database = new ServerDatabaseState
                {
                    Provider = configuration.Provider,
                    ConnectionString = configuration.ConnectionString,
                },
                Execution = new ServerExecutionState
                {
                    Provider = configuration.DeploymentMode == DeploymentMode.Cluster ? "distributed" : "inProcess",
                },
                DistributedLock =
                    configuration.DeploymentMode == DeploymentMode.Cluster
                        ? new ServerDistributedLockState { Provider = "postgres", ConnectionString = string.Empty }
                        : null,
                PasswordHash = passwordHash,
                SessionVersion = 1,
            };
            await WriteAsync(nextState, cancellationToken);
            _state = nextState;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadOnlyList<LegacyApiTokenState> GetLegacyApiTokens()
    {
        return _state
                .Tokens?.Select(token => new LegacyApiTokenState(
                    token.Id,
                    token.Name,
                    token.Prefix,
                    token.SecretHash,
                    token.CreatedAt
                ))
                .ToArray()
            ?? [];
    }

    public async Task ClearLegacyApiTokensAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var stateFileLock = await AcquireStateFileLockAsync(cancellationToken);
            RefreshFromDisk();
            if (_state.Tokens == null)
                return;

            var nextState = Copy(_state);
            nextState.Tokens = null;
            await WriteAsync(nextState, cancellationToken);
            _state = nextState;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var stateFileLock = await AcquireStateFileLockAsync(cancellationToken);
            RefreshFromDisk();
            var nextState = Copy(_state);
            nextState.PasswordHash = passwordHash;
            nextState.SessionVersion++;
            await WriteAsync(nextState, cancellationToken);
            _state = nextState;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteAsync(ServerState state, CancellationToken cancellationToken)
    {
        _paths.EnsureCreated();
        var tempPath = $"{_paths.StateFile}.{Guid.CreateVersion7():N}.tmp";
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            await using (var stream = new FileStream(tempPath, streamOptions))
            {
                await JsonSerializer.SerializeAsync(stream, state, _serializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            if (File.Exists(_paths.StateFile))
            {
                await ReplaceAsync(tempPath, cancellationToken);
            }
            else
            {
                File.Move(tempPath, _paths.StateFile);
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_paths.StateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stateFileLock = await AcquireStateFileLockAsync(cancellationToken).ConfigureAwait(false);
            RefreshFromDisk();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void RefreshFromDisk()
    {
        if (File.Exists(_paths.StateFile))
        {
            _state = Load(_paths.StateFile);
        }
    }

    private async Task<FileStream> AcquireStateFileLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = $"{_paths.StateFile}.lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.Asynchronous,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                return new FileStream(lockPath, options);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken);
            }
        }
    }

    private async Task ReplaceAsync(string tempPath, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Replace(tempPath, _paths.StateFile, null);
                return;
            }
            catch (IOException exception) when (OperatingSystem.IsWindows() && IsSharingViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception) =>
        (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private ServerState Load(string path)
    {
        if (!File.Exists(path))
            return new ServerState();

        using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 4096,
                Options = FileOptions.SequentialScan,
            }
        );
        return JsonSerializer.Deserialize<ServerState>(stream, _serializerOptions) ?? new ServerState();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static ServerState Copy(ServerState state) =>
        new()
        {
            SchemaVersion = state.SchemaVersion,
            IsInitialized = state.IsInitialized,
            Database = new ServerDatabaseState
            {
                Provider = state.Database.Provider,
                ConnectionString = state.Database.ConnectionString,
            },
            Execution =
                state.Execution == null ? null : new ServerExecutionState { Provider = state.Execution.Provider },
            DistributedLock =
                state.DistributedLock == null
                    ? null
                    : new ServerDistributedLockState
                    {
                        Provider = state.DistributedLock.Provider,
                        ConnectionString = state.DistributedLock.ConnectionString,
                    },
            PasswordHash = state.PasswordHash,
            SessionVersion = state.SessionVersion,
            Tokens = state
                .Tokens?.Select(token => new ApiTokenRecord
                {
                    Id = token.Id,
                    Name = token.Name,
                    Prefix = token.Prefix,
                    SecretHash = token.SecretHash,
                    CreatedAt = token.CreatedAt,
                })
                .ToList(),
        };

    private sealed class ServerState
    {
        public int SchemaVersion { get; set; } = 1;
        public bool IsInitialized { get; set; }
        public ServerDatabaseState Database { get; set; } = new();

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ServerExecutionState? Execution { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ServerDistributedLockState? DistributedLock { get; set; }
        public string? PasswordHash { get; set; }
        public int SessionVersion { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ApiTokenRecord>? Tokens { get; set; }
    }

    private sealed class ServerDatabaseState
    {
        public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;
        public string ConnectionString { get; set; } = string.Empty;
    }

    private sealed class ServerExecutionState
    {
        public string Provider { get; set; } = string.Empty;
    }

    private sealed class ServerDistributedLockState
    {
        public string Provider { get; set; } = string.Empty;
        public string ConnectionString { get; set; } = string.Empty;
    }

    private sealed class ApiTokenRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Prefix { get; set; } = string.Empty;
        public string SecretHash { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}

public sealed record LegacyApiTokenState(
    Guid Id,
    string Name,
    string Prefix,
    string SecretHash,
    DateTimeOffset CreatedAt
);
