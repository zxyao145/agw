using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Agw.Extensions.Feishu.Tests")]

namespace Agw.Extensions.Feishu;

internal static class FeishuCallbackRules
{
    public static bool MatchesMessage(
        FeishuApp app,
        string appOpenId,
        string messageId,
        Guid notificationId,
        string dispatchKey,
        FeishuMessage? message,
        FeishuTarget? target
    ) =>
        string.Equals(app.AppId, appOpenId, StringComparison.Ordinal)
        && message is not null
        && target is not null
        && message.RootId == app.Id
        && message.MessageId == messageId
        && message.NotificationId == notificationId
        && string.Equals(message.DispatchKey, dispatchKey, StringComparison.Ordinal)
        && target.RootId == app.Id
        && target.Id == message.TargetId;

    public static bool MatchesIdentity(FeishuApp app, string openId, FeishuIdentityBinding? binding) =>
        binding is not null
        && binding.RootId == app.Id
        && string.Equals(binding.OpenId, openId, StringComparison.Ordinal)
        && string.Equals(binding.UserId, app.CreateBy, StringComparison.Ordinal);
}
