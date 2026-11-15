using Agw.Shared.Tooling;
using Agw.Tools.Abstractions.Generated;
using Agw.Tools.Abstractions.ToolBlocks;

namespace Agw.Notifications.Application.Tools;

internal sealed partial class NotificationToolBlock : IAgwToolBlockDeclaration, IAgwToolSet<NotificationToolExecutor>
{
    public string Name => ToolBlockDefinitionNames.Notification;

    public string DisplayName => "Notification";

    public string Description => "Find notification destinations, queue notices, and check delivery status.";

    public ToolBlockScope Scopes => ToolBlockScope.Agent;
}
