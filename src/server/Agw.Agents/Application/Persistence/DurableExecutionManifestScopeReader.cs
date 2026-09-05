using System.Text.Json;
using Agw.Agents.Execution.Durable;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Application.Persistence;

public sealed record DurableExecutionScope(Guid ProjectId, Guid ProjectConversationId);

public static class DurableExecutionManifestScopeReader
{
    public static DurableExecutionScope? Read(string json, Guid executionId, string ownerUserId)
    {
        try
        {
            var manifest = DurableExecutionJson.DeserializeRequired<DurableExecutionManifest>(
                json,
                "execution manifest"
            );
            if (
                manifest.SchemaVersion != DurableExecutionManifest.CurrentSchemaVersion
                || manifest.ExecutionId != executionId
                || !string.Equals(manifest.ResolveUserId(), ownerUserId, StringComparison.Ordinal)
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
        catch (AgwException exception) when (exception.Code == ErrorCodes.DurableExecutionConflict.Code)
        {
            return null;
        }
    }
}
