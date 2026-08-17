using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Providers;

public class AgwAiModelConfiguration : IEntityTypeConfiguration<AgwAiModel>
{
    public void Configure(EntityTypeBuilder<AgwAiModel> builder)
    {
        builder.ToTable(table => table.HasCheckConstraint(
            "ck_model_token_limits",
            "max_context_window_tokens > 0 AND max_output_tokens > 0 " +
            "AND max_output_tokens < max_context_window_tokens"));
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Description).HasMaxLength(1000);
        builder.Property(e => e.MaxContextWindowTokens)
            .HasColumnName("max_context_window_tokens")
            .HasDefaultValue(AgwAiModel.DefaultMaxContextWindowTokens);
        builder.Property(e => e.MaxOutputTokens)
            .HasColumnName("max_output_tokens")
            .HasDefaultValue(AgwAiModel.DefaultMaxOutputTokens);
    }
}
