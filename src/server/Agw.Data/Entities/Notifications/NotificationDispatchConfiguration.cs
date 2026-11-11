using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Notifications;

public sealed class NotificationDispatchConfiguration : IEntityTypeConfiguration<NotificationDispatch>
{
    public void Configure(EntityTypeBuilder<NotificationDispatch> builder)
    {
        builder.HasKey(dispatch => dispatch.Id);
        builder.Property(dispatch => dispatch.ChannelKey).IsRequired().HasMaxLength(400);
        builder.Property(dispatch => dispatch.State).HasConversion<int>();
        builder.Property(dispatch => dispatch.ExternalMessageId).HasMaxLength(400);
        builder.Property(dispatch => dispatch.LastError).HasMaxLength(2000);
        builder.Property(dispatch => dispatch.ClaimedBy).HasMaxLength(200);
        builder.Property(dispatch => dispatch.ClaimAttemptId).IsConcurrencyToken();
        builder.Property(dispatch => dispatch.ClaimExpiresAt).IsConcurrencyToken();
        builder.HasIndex(dispatch => new { dispatch.NotificationId, dispatch.ChannelKey }).IsUnique();
        builder.HasIndex(dispatch => new { dispatch.State, dispatch.NextAttemptAt });
        builder.HasIndex(dispatch => new { dispatch.State, dispatch.RecoverAfter });
        builder.HasIndex(dispatch => new { dispatch.State, dispatch.ClaimExpiresAt });
    }
}
