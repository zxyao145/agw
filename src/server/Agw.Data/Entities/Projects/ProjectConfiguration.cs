using Agw.Shared.Tooling;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        // UserDefined = 0,
        // DefaultBuiltIn = 1,
        builder.Property(e => e.Type).HasConversion<int>();
        builder.Property(e => e.Description).HasMaxLength(1000);
        builder.Property(e => e.Workspace).HasMaxLength(1000);
        builder.Property(e => e.ExtraSetting).HasMaxLength(16000);
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
    }
}
