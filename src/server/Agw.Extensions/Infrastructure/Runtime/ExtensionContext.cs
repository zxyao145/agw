using System.Text.Json;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk;
using Agw.Shared.Contracts;
using Agw.Shared.Contracts.Coordination;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Infrastructure.Runtime;

internal sealed class ExtensionContext : IExtensionContext
{
    private readonly IServiceProvider _hostServices;
    private readonly Guid _extensionId;
    private IExtensionLeaseManager? _leases;
    private IExtensionSecrets? _secrets;

    public ExtensionContext(
        ExtensionRuntimeLoadRequest request,
        ExtensionGenerationId generationId,
        IExtensionHostCapabilities hostCapabilities,
        ICurrentUser currentUser,
        IServiceProvider hostServices,
        JsonSerializerOptions jsonOptions,
        ILogger logger
    )
    {
        ExtensionId = request.ExtensionId;
        _extensionId = request.ExtensionId;
        _hostServices = hostServices;
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

    public IExtensionLeaseManager Leases =>
        _leases ??= new ExtensionLeaseManager(_extensionId, _hostServices.GetRequiredService<IApplicationLock>());

    public IExtensionSecrets Secrets =>
        _secrets ??= new ExtensionSecrets(_extensionId, _hostServices.GetRequiredService<IDataProtectionProvider>());

    public JsonSerializerOptions JsonOptions { get; }

    public ILogger Logger { get; }

    public IExtensionHostCapabilities HostCapabilities { get; }
}
