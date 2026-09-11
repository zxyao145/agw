using Agw.Tools.Abstractions.Generated;

namespace Agw.Jobs.Application.Tools;

internal sealed partial class JobManagementToolBlock : IAgwToolBlockDeclaration, IAgwToolSet<JobManagementToolExecutor>
{
    public string Name => "agw-job";

    public string DisplayName => "Job management";

    public string Description => "Manage scheduled jobs in the current project.";

    public bool ExcludeFromList => true;
}
