using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Options;

namespace Agw.Extensions.Infrastructure.Runtime;

public sealed class ExtensionHostIdentity : IExtensionHostIdentity, IDisposable
{
    private readonly FileStream _ownership;
    private Func<bool>? _unloadProbe;
    private bool _released;

    public ExtensionHostIdentity(AgwDataPaths paths, IOptions<ExtensionOptions> options)
    {
        Role = options.Value.HostRole;
        if (Role is not ("Standalone" or "ControlPlane" or "DataPlane"))
            throw new AgwException(ErrorCodes.InvalidParam);
        Directory.CreateDirectory(paths.RuntimeDirectory);
        var identityPath = Path.Combine(paths.RuntimeDirectory, $"extension-host-{Role}.json");
        try
        {
            _ownership = new FileStream(
                identityPath + ".lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
        }
        catch (IOException)
        {
            throw new AgwException(
                ErrorCodes.ExtensionLoadFailed,
                "The extension Host identity is already owned or inaccessible."
            );
        }
        try
        {
            if (File.Exists(identityPath))
            {
                using var input = File.OpenRead(identityPath);
                HostId = JsonSerializer.Deserialize<IdentityDocument>(input)?.HostId ?? "";
                if (!Guid.TryParseExact(HostId, "D", out var id) || id.Version != 7 || id.ToString() != HostId)
                    throw new AgwException(
                        ErrorCodes.ExtensionLoadFailed,
                        "Persisted extension Host identity is invalid."
                    );
            }
            else
            {
                HostId = Guid.CreateVersion7().ToString();
                var stage = identityPath + ".new";
                using (var output = new FileStream(stage, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(output, new IdentityDocument(HostId));
                    output.Flush(flushToDisk: true);
                }
                File.Move(stage, identityPath);
            }
        }
        catch
        {
            _ownership.Dispose();
            throw;
        }
        AppDomain.CurrentDomain.ProcessExit += ReleaseAtProcessExit;
    }

    public string HostId { get; }
    public string Role { get; }

    internal void SetUnloadProbe(Func<bool> probe) => _unloadProbe = probe;

    public void Dispose()
    {
        if (_unloadProbe?.Invoke() == false)
            return;
        Release();
    }

    private void ReleaseAtProcessExit(object? sender, EventArgs args) => Release();

    private void Release()
    {
        if (_released)
            return;
        _released = true;
        _ownership.Dispose();
        _unloadProbe = null;
        AppDomain.CurrentDomain.ProcessExit -= ReleaseAtProcessExit;
    }

    private sealed record IdentityDocument(string HostId);
}
