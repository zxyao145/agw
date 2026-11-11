namespace Agw.Notifications.Configuration;

public sealed class NotificationDispatchOptions
{
    public int MaxAttempts { get; set; } = 8;

    public TimeSpan ClaimDuration { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan RecoveryStaleAfter { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(30);
}
