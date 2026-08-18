using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agents;

public class McpServerConfiguration : IEntityTypeConfiguration<McpServer>
{
    public void Configure(EntityTypeBuilder<McpServer> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Description).HasMaxLength(1000);
        builder.Property(e => e.TransportType).IsRequired().HasMaxLength(20);
        builder.Property(e => e.Command).HasMaxLength(200);
        builder.Property(e => e.WorkingDirectory).HasMaxLength(500);
        builder.Property(e => e.Url).HasMaxLength(1000);
        builder
            .Property(e => e.Arguments)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v =>
                    string.IsNullOrWhiteSpace(v)
                        ? new List<string>()
                        : System.Text.Json.JsonSerializer.Deserialize<List<string>>(
                            v,
                            (System.Text.Json.JsonSerializerOptions?)null
                        ) ?? new List<string>()
            );
        builder
            .Property(e => e.EnvironmentVariables)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v =>
                    string.IsNullOrWhiteSpace(v)
                        ? new Dictionary<string, string>()
                        : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                            v,
                            (System.Text.Json.JsonSerializerOptions?)null
                        ) ?? new Dictionary<string, string>()
            );
        var headersProperty = builder
            .Property(e => e.Headers)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v =>
                    string.IsNullOrWhiteSpace(v)
                        ? new Dictionary<string, string>()
                        : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                            v,
                            (System.Text.Json.JsonSerializerOptions?)null
                        ) ?? new Dictionary<string, string>()
            );
        headersProperty.Metadata.SetValueComparer(
            new ValueComparer<Dictionary<string, string>>(
                (left, right) =>
                    left != null
                    && right != null
                    && left.OrderBy(pair => pair.Key).SequenceEqual(right.OrderBy(pair => pair.Key)),
                value =>
                    value
                        .OrderBy(pair => pair.Key)
                        .Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value)),
                value => new Dictionary<string, string>(value, value.Comparer)
            )
        );
    }
}
