using System.Text.Json;
using Agw.Shared.Runtime;

namespace Agw.Host.Runtime;

public sealed class ServerRuntimeDescriptorStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly AgwDataPaths _paths;

    public ServerRuntimeDescriptorStore(AgwDataPaths paths)
    {
        _paths = paths;
    }

    public async Task WriteAsync(ServerRuntimeDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.RuntimeDirectory);
        var tempFile = $"{_paths.ServerRuntimeFile}.{Guid.CreateVersion7():N}.tmp";
        try
        {
            await using (
                var stream = new FileStream(
                    tempFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    useAsync: true
                )
            )
            {
                await JsonSerializer.SerializeAsync(stream, descriptor, SerializerOptions, cancellationToken);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(tempFile, _paths.ServerRuntimeFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    public async Task DeleteIfOwnedAsync(int pid, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.ServerRuntimeFile))
            return;

        try
        {
            await using var stream = File.OpenRead(_paths.ServerRuntimeFile);
            var descriptor = await JsonSerializer.DeserializeAsync<ServerRuntimeDescriptor>(
                stream,
                SerializerOptions,
                cancellationToken
            );
            if (descriptor?.Pid == pid)
                File.Delete(_paths.ServerRuntimeFile);
        }
        catch (JsonException)
        {
            // A newer or partially-written descriptor is not owned by this process.
        }
        catch (IOException)
        {
            // Shutdown cleanup is best-effort; a newer process may already own the file.
        }
    }
}
