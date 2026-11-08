using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionContext : IExtensionContext
{
    public ExtensionContext(
        ExtensionRuntimeLoadRequest request,
        ExtensionGenerationId generationId,
        IExtensionHostCapabilities hostCapabilities,
        ICurrentUser currentUser,
        JsonSerializerOptions jsonOptions,
        ILogger logger
    )
    {
        ExtensionId = request.ExtensionId;
        Name = request.Name;
        Version = request.Version;
        ReleaseHash = request.ReleaseHash;
        GenerationId = generationId.GenerationId;
        Epoch = request.Epoch;
        DataDirectory = request.DataDirectory;
        HostCapabilities = hostCapabilities;
        CurrentUser = currentUser;
        JsonOptions = jsonOptions;
        Logger = new ExtensionBoundaryLogger(logger);
    }

    public Guid ExtensionId { get; }

    public string Name { get; }

    public string Version { get; }

    public string ReleaseHash { get; }

    public Guid GenerationId { get; }

    public long Epoch { get; }

    public string DataDirectory { get; }

    public ICurrentUser CurrentUser { get; }

    public JsonSerializerOptions JsonOptions { get; }

    public ILogger Logger { get; }

    public IExtensionHostCapabilities HostCapabilities { get; }
}
