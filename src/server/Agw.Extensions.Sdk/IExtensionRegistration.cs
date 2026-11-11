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

public delegate ValueTask<ExtensionEventHandlerResult> ExtensionEventHandler(
    IServiceProvider services,
    IExtensionContext context,
    ExtensionEventDelivery delivery,
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

    void RegisterEventHandler(ExtensionEventHandlerDescriptor descriptor, ExtensionEventHandler handler);

    void RegisterEventSourceProvider<TProvider>(ExtensionEventSourceProviderDescriptor descriptor)
        where TProvider : class, IExtensionEventSourceProvider;

    void RegisterNotificationChannel<TChannel>(ExtensionNotificationChannelDescriptor descriptor)
        where TChannel : class, IExtensionNotificationChannel;

    void RegisterOutboxResultHandler<THandler>(string kind)
        where THandler : class, IExtensionOutboxResultHandler;

    void RegisterBackgroundService<TService>()
        where TService : class, IExtensionBackgroundService;

    void RegisterDatabase(IExtensionDatabaseFactory factory);
}
