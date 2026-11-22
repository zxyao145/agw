using System.Text.Json;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Agents;

public sealed class AgentA2AEndpointConfiguration : IEntityTypeConfiguration<AgentA2AEndpoint>
{
    public void Configure(EntityTypeBuilder<AgentA2AEndpoint> builder)
    {
        builder.HasKey(endpoint => endpoint.AgentId);
        builder.Property(endpoint => endpoint.AgentId).ValueGeneratedNever();
        builder
            .HasOne(endpoint => endpoint.Agent)
            .WithOne(agent => agent.A2AEndpoint)
            .HasForeignKey<AgentA2AEndpoint>(endpoint => endpoint.AgentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Property(endpoint => endpoint.CardUrl).HasMaxLength(2048);
        builder.Property(endpoint => endpoint.InterfaceUrl).HasMaxLength(2048);
        builder.Property(endpoint => endpoint.ProtocolBinding).HasMaxLength(32);
        builder
            .Property(endpoint => endpoint.Credentials)
            .HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                text => ReadCredentials(text),
                new ValueComparer<Dictionary<string, string>>(
                    (left, right) =>
                        left == right
                        || left != null
                            && right != null
                            && left.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                                .SequenceEqual(right.OrderBy(pair => pair.Key, StringComparer.Ordinal)),
                    value => value.Aggregate(0, (hash, pair) => hash ^ HashCode.Combine(pair.Key, pair.Value)),
                    value => new Dictionary<string, string>(value, value.Comparer)
                )
            )
            .HasDefaultValueSql("'{}'");
        builder
            .Property(endpoint => endpoint.Extra)
            .HasConversion(ExtraJson.Converter<AgentA2AEndpointExtra>())
            .HasDefaultValueSql("'{}'");
    }

    private static Dictionary<string, string> ReadCredentials(string text) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(text)
        ?? throw new AgwException(ErrorCodes.EncryptedDataInvalid);
}
