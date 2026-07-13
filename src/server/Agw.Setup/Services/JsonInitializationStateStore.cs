using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Agw.Infrastructure.Configuration;
using Agw.Setup.Contracts;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;

namespace Agw.Setup.Services;

public sealed class JsonInitializationStateStore : IInitializationStateStore, IServerInitializationState
{
    private readonly AgwDataPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private volatile ServerState _state;

    public JsonInitializationStateStore(AgwDataPaths paths, TimeProvider timeProvider)
    {
        _paths = paths;
        _timeProvider = timeProvider;
        _state = Load(paths.StateFile);
    }

    public InitializationSnapshot GetSnapshot()
    {
        var state = _state;
        return new InitializationSnapshot(
            state.IsInitialized,
            state.PasswordHash,
            state.SessionVersion,
            state.Tokens.Select(ToSummary).ToArray());
    }

    public bool IsInitialized => _state.IsInitialized;

    public async Task PersistAsync(SetupRequest request, string passwordHash, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var nextState = new ServerState
            {
                SchemaVersion = 1,
                IsInitialized = true,
                Database = new DatabaseSettings { Provider = request.Provider, ConnectionString = request.ConnectionString },
                PasswordHash = passwordHash,
                SessionVersion = 1,
                Tokens = []
            };
            await WriteAsync(nextState, cancellationToken);
            _state = nextState;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<CreatedApiToken> CreateTokenAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalizedName = name.Trim();
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var currentState = _state;
            if (currentState.Tokens.Any(x => string.Equals(x.Name, normalizedName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new AgwException(ErrorCodes.ApiTokenNameAlreadyExists);
            }

            var token = $"agw_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
            var record = new ApiTokenRecord
            {
                Id = Guid.NewGuid(),
                Name = normalizedName,
                Prefix = token[..Math.Min(token.Length, 12)],
                SecretHash = Hash(token),
                CreatedAt = _timeProvider.GetUtcNow()
            };
            var nextState = Copy(currentState);
            nextState.Tokens.Add(record);
            await WriteAsync(nextState, cancellationToken);
            _state = nextState;
            return new CreatedApiToken(record.Id, record.Name, record.Prefix, record.CreatedAt, token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> RevokeTokenAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var nextState = Copy(_state);
            var removed = nextState.Tokens.RemoveAll(x => x.Id == id) > 0;
            if (removed)
            {
                await WriteAsync(nextState, cancellationToken);
                _state = nextState;
            }
            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public bool ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("agw_", StringComparison.Ordinal)) return false;
        var candidate = Convert.FromHexString(Hash(token));
        return _state.Tokens.Any(x => CryptographicOperations.FixedTimeEquals(candidate, Convert.FromHexString(x.SecretHash)));
    }

    public async Task UpdatePasswordAsync(string passwordHash, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
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
        var tempPath = $"{_paths.StateFile}.{Guid.NewGuid():N}.tmp";
        var streamOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.WriteThrough
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
                File.Replace(tempPath, _paths.StateFile, null);
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
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private ServerState Load(string path)
    {
        if (!File.Exists(path)) return new ServerState();
        return JsonSerializer.Deserialize<ServerState>(File.ReadAllText(path), _serializerOptions) ?? new ServerState();
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static ApiTokenSummary ToSummary(ApiTokenRecord token) => new(token.Id, token.Name, token.Prefix, token.CreatedAt);

    private static ServerState Copy(ServerState state) => new()
    {
        SchemaVersion = state.SchemaVersion,
        IsInitialized = state.IsInitialized,
        Database = new DatabaseSettings
        {
            Provider = state.Database.Provider,
            ConnectionString = state.Database.ConnectionString
        },
        PasswordHash = state.PasswordHash,
        SessionVersion = state.SessionVersion,
        Tokens = state.Tokens.Select(token => new ApiTokenRecord
        {
            Id = token.Id,
            Name = token.Name,
            Prefix = token.Prefix,
            SecretHash = token.SecretHash,
            CreatedAt = token.CreatedAt
        }).ToList()
    };

    private sealed class ServerState
    {
        public int SchemaVersion { get; set; } = 1;
        public bool IsInitialized { get; set; }
        public DatabaseSettings Database { get; set; } = new();
        public string? PasswordHash { get; set; }
        public int SessionVersion { get; set; }
        public List<ApiTokenRecord> Tokens { get; set; } = [];
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
