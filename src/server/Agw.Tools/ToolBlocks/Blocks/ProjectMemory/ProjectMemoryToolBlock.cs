using Agw.Files.Abstracts;
using Agw.Files.Utils;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Agw.Tools.ToolBlocks.Storage;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.ToolBlocks.Blocks.ProjectMemory;

public sealed class ProjectMemoryToolBlock : IToolBlock
{
    internal const string FileSystemRoot = ".agw/memory";

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly IApplicationLock _applicationLock;

    public ProjectMemoryToolBlock(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        IAgwFileSystemResolver fileSystemResolver
    )
        : this(serviceScopeFactory, timeProvider, fileSystemResolver, InMemoryApplicationLock.Shared) { }

    public ProjectMemoryToolBlock(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        IAgwFileSystemResolver fileSystemResolver,
        IApplicationLock applicationLock
    )
    {
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _fileSystemResolver = fileSystemResolver;
        _applicationLock = applicationLock;
    }

    public ToolBlockDescriptor Descriptor { get; } =
        new(
            ToolBlockNames.ProjectMemory,
            "Project Memory",
            "Provides database-backed or workspace-backed memory shared across the current project.",
            ToolBlockScope.Agent | ToolBlockScope.Project,
            [
                ProjectMemoryProvider.WriteToolName,
                ProjectMemoryProvider.ReadFileToolName,
                ProjectMemoryProvider.DeleteFileToolName,
                ProjectMemoryProvider.LsToolName,
                ProjectMemoryProvider.GrepToolName,
                ProjectMemoryProvider.ReplaceToolName,
                ProjectMemoryProvider.ReplaceLinesToolName,
            ]
        );

    public ValueTask<ToolContribution> MaterializeAsync(
        ToolBlockDefinition definition,
        ToolMaterializationContext context,
        CancellationToken cancellationToken
    )
    {
        if (definition is not ProjectMemoryToolBlockDefinition { Options: not null } projectMemoryDefinition)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool Block '{definition.GetDefinitionName()}' does not contain project memory options."
            );
        }

        var contribution = new ToolContribution();
        var storage = projectMemoryDefinition.Options.Storage;
        AgentFileStore store = storage switch
        {
            ProjectMemoryStorage.Database => new EfProjectMemoryStore(
                _serviceScopeFactory,
                _timeProvider,
                _applicationLock,
                context.ProjectId
            ),
            ProjectMemoryStorage.FileSystem => new ProjectAgentFileStore(
                _fileSystemResolver,
                context.ProjectId,
                FileSystemRoot
            ),
            _ => throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Project Memory storage '{storage}' is not supported."
            ),
        };
        contribution.PlanModeAllowedToolNames.UnionWith([
            ProjectMemoryProvider.ReadFileToolName,
            ProjectMemoryProvider.LsToolName,
            ProjectMemoryProvider.GrepToolName,
        ]);
        contribution.ContextProviders.Add(
            new ProjectMemoryProvider(store, _applicationLock, GetMutationResourceName(context, storage))
        );
        return ValueTask.FromResult(contribution);
    }

    internal static string GetMutationResourceName(ToolMaterializationContext context, ProjectMemoryStorage storage)
    {
        if (storage == ProjectMemoryStorage.Database)
        {
            return $"project-memory:database:{context.ProjectId:D}";
        }

        var workspace = string.IsNullOrWhiteSpace(context.Project.Workspace)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agw",
                context.Project.Name
            )
            : PathUtil.ExpandTilde(context.Project.Workspace);
        var normalizedWorkspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (OperatingSystem.IsWindows())
        {
            normalizedWorkspace = normalizedWorkspace.ToUpperInvariant();
        }

        return $"project-memory:filesystem:{normalizedWorkspace}";
    }
}
