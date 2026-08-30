using Agw.Shared.Tooling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agents;

public class AgentConfiguration : IEntityTypeConfiguration<Agent>
{
    public void Configure(EntityTypeBuilder<Agent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.CreateBy, e.Name }).IsUnique();
        builder.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Description).HasMaxLength(200);
        builder.Property(e => e.SystemPrompt).HasMaxLength(4000);
        var tools = builder
            .Property(e => e.Tools)
            .HasConversion(
                value => ToolValueObjectJson.Serialize(value),
                value => ToolValueObjectJson.Deserialize(value)
            )
            .HasMaxLength(16000)
            .IsRequired();
        tools.Metadata.SetValueComparer(
            new ValueComparer<List<ToolValueObject>>(
                (left, right) => ToolValueObjectJson.SequenceEqual(left, right),
                value => ToolValueObjectJson.GetSequenceHashCode(value),
                value => ToolValueObjectJson.Clone(value)
            )
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

        // ModelProviderId is optional - required for System agents, optional for External agents
        //builder.HasOne(e => e.ModelProvider)
        //    .WithMany(p => p.Agents)
        //    .HasForeignKey(e => e.ModelProviderId)
        //    .OnDelete(DeleteBehavior.Cascade)
        //    .IsRequired(false);
    }
}
