using System.Text.Json;

using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;

namespace Agw.Integrations.Tests;

public sealed class CredentialSerializationTests
{
    [Fact]
    public void Serialize_ConnectionCredential_DoesNotExposeStoredSecretFields()
    {
        var credential = new ConnectionCredential
        {
            Id = Guid.NewGuid(),
            ConnectionId = Guid.NewGuid(),
            Slot = "api-key",
            Value = "protected-secret-sentinel",
            MetadataJson = "{\"secret\":\"metadata-sentinel\"}",
        };

        var json = JsonSerializer.Serialize(credential);

        Assert.DoesNotContain("protected-secret-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("protectedValue", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("metadataJson", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialize_AgentConnectionRelation_DoesNotTraverseConnectionOrCredentials()
    {
        var relation = new AgentConnectionRelation
        {
            AgentId = Guid.NewGuid(),
            ConnectionId = Guid.NewGuid(),
            Connection = new Connection
            {
                Id = Guid.NewGuid(),
                Alias = "private",
                Credentials =
                [
                    new ConnectionCredential
                    {
                        Value = "relation-secret-sentinel",
                    },
                ],
            },
        };

        var json = JsonSerializer.Serialize(relation);

        Assert.DoesNotContain("private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("relation-secret-sentinel", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Connection\":", json, StringComparison.OrdinalIgnoreCase);
    }
}
