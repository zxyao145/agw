using Agw.Files.Abstracts;
using Agw.Shared.Exceptions;

namespace Agw.Files.Application.Files;

public sealed class RepositoryWorkspaceAppService
{
    private readonly IAgwFileSystemResolver _resolver;
    private readonly IRepositoryWorkspace _workspace;

    public RepositoryWorkspaceAppService(IAgwFileSystemResolver resolver, IRepositoryWorkspace workspace)
    {
        _resolver = resolver;
        _workspace = workspace;
    }

    public async Task<RepositoryWorkspaceState> ReadAsync(Guid projectId, CancellationToken ct) =>
        await _workspace.ReadAsync(await ResolveRootAsync(projectId, ct), ct);

    public async Task<RepositoryWorkspaceState> ExecuteAsync(
        string operation,
        RepositoryWorkspaceRequest request,
        CancellationToken ct
    ) => await _workspace.ExecuteAsync(await ResolveRootAsync(request.ProjectId, ct), operation, request, ct);

    private async Task<string> ResolveRootAsync(Guid projectId, CancellationToken ct)
    {
        var fileSystem = await _resolver.ResolveAsync(projectId, ct);
        if (fileSystem == null)
            throw new AgwException(ErrorCodes.ResourceNotFound);
        if (fileSystem is not ILocalFileSystem local)
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "Repository workspaces require a local project file system."
            );
        return local.ResolvePhysicalPath("");
    }
}
