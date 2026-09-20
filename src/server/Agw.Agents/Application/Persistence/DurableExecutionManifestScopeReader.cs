using System.Text.Json;
using Agw.Shared.Utils;

namespace Agw.Agents.Application.Persistence;

public sealed record DurableExecutionScope(Guid ProjectId, Guid ProjectConversationId);

public static class DurableExecutionManifestScopeReader
{
    public static DurableExecutionScope? Read(string json, Guid executionId, string ownerUserId)
    {
        try
        {
            var manifest = JsonUtil.Deserialize<DurableExecutionManifest>(json);
            if (
                manifest == null
                || manifest.SchemaVersion != DurableExecutionManifest.CurrentSchemaVersion
                || manifest.ExecutionId != executionId
                || string.IsNullOrWhiteSpace(manifest.UserId)
                || !string.Equals(manifest.UserId, ownerUserId, StringComparison.Ordinal)
                || manifest.WorkspaceSnapshot == null
                || manifest.Task == null
                || manifest.Input == null
                || manifest.Settings == null
                || manifest.Task.ProjectId == Guid.Empty
                || manifest.Task.ProjectConversationId == Guid.Empty
            )
            {
                return null;
            }

            return new DurableExecutionScope(manifest.Task.ProjectId, manifest.Task.ProjectConversationId);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
