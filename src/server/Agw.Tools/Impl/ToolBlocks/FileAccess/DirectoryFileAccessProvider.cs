using System.Text.Json;
using System.Text.Json.Nodes;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Agw.Tools.Impl.ToolBlocks.Storage;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.ToolBlocks.FileAccess;

/// <summary>Routes the existing SDK file tools to a directory in the captured workspace.</summary>
internal sealed class DirectoryFileAccessProvider : AIContextProvider, IAsyncDisposable
{
    private readonly Dictionary<string, FileAccessProvider> _providers = new(StringComparer.Ordinal);

    public DirectoryFileAccessProvider(
        IAgwFileSystemResolver resolver,
        Guid projectId,
        ProjectWorkspaceSnapshot snapshot
    )
    {
        AddProvider("primary", null);
        foreach (var directory in snapshot.AdditionalDirectories)
        {
            AddProvider(directory.Id.ToString("D"), directory.Id);
        }

        void AddProvider(string key, Guid? directoryId)
        {
            _providers.Add(
                key,
                new FileAccessProvider(
                    new ProjectAgentFileStore(resolver, projectId, null, snapshot, directoryId),
                    new FileAccessProviderOptions
                    {
                        DisableReadOnlyToolApproval = true,
                        DisableWriteToolApproval = true,
                    }
                )
            );
        }
    }

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default
    )
    {
        var toolsByDirectory = new Dictionary<string, Dictionary<string, AIFunction>>(StringComparer.Ordinal);
        string? instructions = null;
        foreach (var (key, provider) in _providers)
        {
            // InvokingAsync returns a merged context, but this method must return only our contribution.
            // Isolate each directory so upstream tools and instructions are not wrapped and emitted again.
            var provided = await provider
                .InvokingAsync(new InvokingContext(context.Agent, context.Session, new AIContext()), cancellationToken)
                .ConfigureAwait(false);
            instructions ??= provided.Instructions;
            toolsByDirectory[key] = provided
                .Tools!.OfType<AIFunction>()
                .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        }

        return new AIContext
        {
            Instructions =
                instructions
                + "\nFile paths are relative to the selected Project directory. Omit directoryId for the primary directory; use a directoryId from the Project directory list for an additional directory.",
            Tools = toolsByDirectory["primary"]
                .Values.Select(tool => (AITool)new DirectoryFunction(tool, toolsByDirectory))
                .ToArray(),
        };
    }

    public ValueTask DisposeAsync()
    {
        foreach (var provider in _providers.Values)
        {
            provider.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private sealed class DirectoryFunction : DelegatingAIFunction
    {
        private readonly IReadOnlyDictionary<string, Dictionary<string, AIFunction>> _tools;
        private readonly JsonElement _schema;

        public DirectoryFunction(AIFunction inner, IReadOnlyDictionary<string, Dictionary<string, AIFunction>> tools)
            : base(inner)
        {
            _tools = tools;
            var schema = JsonNode.Parse(inner.JsonSchema.GetRawText())!.AsObject();
            schema["properties"]!.AsObject()["directoryId"] = new JsonObject
            {
                ["type"] = new JsonArray("string", "null"),
                ["description"] = "Additional Project directory ID. Omit or use null for the primary directory.",
            };
            _schema = JsonSerializer.SerializeToElement(schema);
        }

        public override JsonElement JsonSchema => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            var key = "primary";
            if (
                arguments.TryGetValue("directoryId", out var value)
                && value != null
                && value is not JsonElement { ValueKind: JsonValueKind.Null }
            )
            {
                if (!Guid.TryParse(value.ToString(), out var directoryId) || directoryId == Guid.Empty)
                {
                    throw new AgwException(ErrorCodes.InvalidParam, "Invalid Project directory ID.");
                }
                key = directoryId.ToString("D");
            }
            if (!_tools.TryGetValue(key, out var tools))
            {
                throw new AgwException(ErrorCodes.ResourceNotFound, "Project directory was not found.");
            }
            var forwarded = new Dictionary<string, object?>(arguments, StringComparer.Ordinal);
            forwarded.Remove("directoryId");
            return tools[Name]
                .InvokeAsync(new AIFunctionArguments(forwarded) { Services = arguments.Services }, cancellationToken);
        }
    }
}
