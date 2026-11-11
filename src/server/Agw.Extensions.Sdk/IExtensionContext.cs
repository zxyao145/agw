using System.Text.Json;
using Agw.Shared.Contracts;
using Microsoft.Extensions.Logging;

namespace Agw.Extensions.Sdk;

public interface IExtensionContext
{
    Guid ExtensionId { get; }

    string Name { get; }

    string Version { get; }

    string ReleaseHash { get; }

    Guid GenerationId { get; }

    long Epoch { get; }

    string DataDirectory { get; }

    ICurrentUser CurrentUser { get; }

    IExtensionLeaseManager Leases { get; }

    IExtensionSecrets Secrets { get; }

    JsonSerializerOptions JsonOptions { get; }

    ILogger Logger { get; }

    IExtensionHostCapabilities HostCapabilities { get; }
}
