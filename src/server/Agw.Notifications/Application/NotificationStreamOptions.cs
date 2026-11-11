namespace Agw.Notifications.Application;

public sealed class NotificationStreamOptions
{
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}
