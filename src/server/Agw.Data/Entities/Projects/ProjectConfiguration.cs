using Microsoft.EntityFrameworkCore;
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
        builder.Property(e => e.Tools).HasMaxLength(4000);
        builder.Property(e => e.EnvironmentVariables).HasConversion(
            v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
            v => string.IsNullOrWhiteSpace(v)
                ? new Dictionary<string, string>()
                : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v,
                      (System.Text.Json.JsonSerializerOptions?)null)
                  ?? new Dictionary<string, string>());
    }
}
