using System.Text.Json;
using System.Text.Json.Serialization;
using Agw.Extensions.Contracts;
using Agw.Extensions.Sdk.Database;

namespace Agw.Extensions.Sdk;

public delegate ValueTask<ExtensionEndpointResponse> ExtensionEndpointHandler(
    IServiceProvider services,
    IExtensionContext context,
    ExtensionEndpointRequest request,
    CancellationToken cancellationToken
);

public delegate ValueTask<string> ExtensionSkillContentProvider(
    IServiceProvider services,
    IExtensionContext context,
    CancellationToken cancellationToken
);

public interface IExtensionRegistration
{
    JsonSerializerOptions JsonOptions { get; }

    /// <summary>Registers generation-owned source-generated JSON metadata. 注册 generation 自有的 source-generated JSON metadata。</summary>
    void RegisterJsonContext(JsonSerializerContext context);

    void RegisterEndpoint(ExtensionEndpointDescriptor descriptor, ExtensionEndpointHandler handler);

    void RegisterTool<TTool>(ExtensionToolDescriptor descriptor)
        where TTool : class, IExtensionTool;

    void RegisterSkill(ExtensionSkillDescriptor descriptor, ExtensionSkillContentProvider contentProvider);

    void RegisterBackgroundService<TService>()
        where TService : class, IExtensionBackgroundService;

    void RegisterDatabase(IExtensionDatabaseFactory factory);
}
