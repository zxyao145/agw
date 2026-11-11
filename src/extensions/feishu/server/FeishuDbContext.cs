using Agw.Extensions.Sdk.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Agw.Extensions.Feishu;

public sealed class FeishuDbContext : ExtensionDbContext
{
    private static readonly ValueConverter<DateTimeOffset, long> UtcTicksConverter = new(
        value => value.UtcTicks,
        ticks => new DateTimeOffset(ticks, TimeSpan.Zero)
    );

    private static readonly ValueConverter<DateTimeOffset?, long?> NullableUtcTicksConverter = new(
        value => value.HasValue ? value.Value.UtcTicks : null,
        ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null
    );

    public FeishuDbContext(
        DbContextOptions<FeishuDbContext> options,
        IExtensionDatabaseIdentity identity,
        IExtensionUserContext userContext,
        TimeProvider timeProvider
    )
        : base(options, identity, userContext, timeProvider) { }

    public DbSet<FeishuApp> Apps => Set<FeishuApp>();

    public DbSet<FeishuTarget> Targets => Set<FeishuTarget>();

    public DbSet<FeishuIdentityBinding> IdentityBindings => Set<FeishuIdentityBinding>();

    public DbSet<FeishuBindRequest> BindRequests => Set<FeishuBindRequest>();

    public DbSet<FeishuMessage> Messages => Set<FeishuMessage>();

    public DbSet<FeishuCallback> Callbacks => Set<FeishuCallback>();

    protected override void ConfigureExtensionModel(ModelBuilder modelBuilder)
    {
        string provider = Database.ProviderName!;
        string indexPrefix = $"zext_{ExtensionDatabaseIdentity.ExtensionId:N}_";

        modelBuilder.Entity<FeishuApp>(entity =>
        {
            ConfigureOwned(entity, "app", provider);
            entity.Property(row => row.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(row => row.Mode).HasColumnName("mode").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.OpenDomain).HasColumnName("open_domain").HasMaxLength(100).IsRequired();
            entity.Property(row => row.WebhookUrl).HasColumnName("webhook_url").HasColumnType("text");
            entity.Property(row => row.WebhookSecret).HasColumnName("webhook_secret").HasColumnType("text");
            entity.Property(row => row.AppId).HasColumnName("app_id").HasMaxLength(100);
            entity.Property(row => row.AppSecret).HasColumnName("app_secret").HasColumnType("text");
            entity.Property(row => row.EncryptKey).HasColumnName("encrypt_key").HasColumnType("text");
            entity.Property(row => row.VerificationToken).HasColumnName("verification_token").HasColumnType("text");
            entity.Property(row => row.Version).HasColumnName("version").IsConcurrencyToken();
            entity.Property(row => row.Enabled).HasColumnName("enabled");
        });

        modelBuilder.Entity<FeishuTarget>(entity =>
        {
            ConfigureChild(entity, "target", provider);
            entity.Property(row => row.RootId).HasColumnName("app_id");
            entity.Property(row => row.Kind).HasColumnName("kind").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.ReceiveId).HasColumnName("receive_id").HasMaxLength(200).IsRequired();
            entity.Property(row => row.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity
                .HasIndex(row => new
                {
                    row.RootId,
                    row.Kind,
                    row.ReceiveId,
                })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_target_kind_receive");
            entity.HasIndex(row => row.RootId).HasDatabaseName($"{indexPrefix}ix_target_app");
        });

        modelBuilder.Entity<FeishuIdentityBinding>(entity =>
        {
            ConfigureChild(entity, "identity_binding", provider);
            entity.Property(row => row.RootId).HasColumnName("app_id");
            entity.Property(row => row.OpenId).HasColumnName("open_id").HasMaxLength(200).IsRequired();
            entity.Property(row => row.UserId).HasColumnName("user_id").HasMaxLength(200).IsRequired();
            entity
                .HasIndex(row => new { row.RootId, row.OpenId })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_identity_app_open");
        });

        modelBuilder.Entity<FeishuBindRequest>(entity =>
        {
            ConfigureChild(entity, "bind_request", provider);
            entity.Property(row => row.RootId).HasColumnName("app_id");
            entity.Property(row => row.CodeHash).HasColumnName("code_hash").HasMaxLength(128).IsRequired();
            entity.Property(row => row.OpenId).HasColumnName("open_id").HasMaxLength(200);
            ConfigureDateTime(entity.Property(row => row.ExpiresAt).HasColumnName("expires_at"), provider);
            entity.HasIndex(row => row.CodeHash).IsUnique().HasDatabaseName($"{indexPrefix}uq_bind_request_code_hash");
            entity.HasIndex(row => new { row.RootId, row.ExpiresAt }).HasDatabaseName($"{indexPrefix}ix_bind_app_exp");
        });

        modelBuilder.Entity<FeishuMessage>(entity =>
        {
            ConfigureChild(entity, "message", provider);
            entity.Property(row => row.RootId).HasColumnName("app_id");
            entity.Property(row => row.DispatchKey).HasColumnName("dispatch_key").HasMaxLength(400).IsRequired();
            entity.Property(row => row.NotificationId).HasColumnName("notification_id");
            entity.Property(row => row.TargetId).HasColumnName("target_id");
            entity.Property(row => row.MessageId).HasColumnName("message_id").HasMaxLength(200).IsRequired();
            entity.Property(row => row.CardVersion).HasColumnName("card_version");
            entity.HasIndex(row => row.DispatchKey).IsUnique().HasDatabaseName($"{indexPrefix}uq_message_dispatch_key");
            entity
                .HasIndex(row => new { row.RootId, row.MessageId })
                .IsUnique()
                .HasDatabaseName($"{indexPrefix}uq_message_app_message");
        });

        modelBuilder.Entity<FeishuCallback>(entity =>
        {
            ConfigureChild(entity, "callback", provider);
            entity.Property(row => row.RootId).HasColumnName("app_id");
            entity.Property(row => row.EventId).HasColumnName("event_id").HasMaxLength(200).IsRequired();
            entity.Property(row => row.PayloadJson).HasColumnName("payload_json").HasColumnType("text").IsRequired();
            entity.Property(row => row.State).HasColumnName("state").HasConversion<string>().HasColumnType("text");
            entity.Property(row => row.OpenId).HasColumnName("open_id").HasMaxLength(200);
            entity.Property(row => row.NotificationId).HasColumnName("notification_id");
            entity.Property(row => row.TargetId).HasColumnName("target_id");
            entity.Property(row => row.MessageId).HasColumnName("message_id").HasMaxLength(200);
            entity.Property(row => row.UserId).HasColumnName("user_id").HasMaxLength(200);
            entity.Property(row => row.OutboxId).HasColumnName("outbox_id");
            entity.Property(row => row.Option).HasColumnName("option");
            entity.Property(row => row.Text).HasColumnName("text").HasColumnType("text");
            entity.HasIndex(row => row.EventId).IsUnique().HasDatabaseName($"{indexPrefix}uq_callback_event_id");
        });
    }

    private static void ConfigureOwned<TEntity>(EntityTypeBuilder<TEntity> entity, string table, string provider)
        where TEntity : FeishuOwnedEntity
    {
        ConfigureBase(entity, table, provider);
    }

    private static void ConfigureChild<TEntity>(EntityTypeBuilder<TEntity> entity, string table, string provider)
        where TEntity : FeishuAppChild
    {
        ConfigureBase(entity, table, provider);
    }

    private static void ConfigureBase<TEntity>(EntityTypeBuilder<TEntity> entity, string table, string provider)
        where TEntity : FeishuOwnedEntity
    {
        entity.ToTable(table);
        entity.HasKey(row => row.Id);
        entity.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
        entity.Property(row => row.CreateBy).HasColumnName("create_by").IsRequired();
        ConfigureDateTime(entity.Property(row => row.CreateTime).HasColumnName("create_time"), provider);
        entity.Property(row => row.UpdateBy).HasColumnName("update_by");
        ConfigureNullableDateTime(entity.Property(row => row.UpdateTime).HasColumnName("update_time"), provider);
    }

    private static void ConfigureDateTime(PropertyBuilder<DateTimeOffset> property, string provider)
    {
        if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            property.HasConversion(UtcTicksConverter).HasColumnType("INTEGER");
        }
        else
        {
            property.HasColumnType("timestamp with time zone");
        }
    }

    private static void ConfigureNullableDateTime(PropertyBuilder<DateTimeOffset?> property, string provider)
    {
        if (provider == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            property.HasConversion(NullableUtcTicksConverter).HasColumnType("INTEGER");
        }
        else
        {
            property.HasColumnType("timestamp with time zone");
        }
    }
}
