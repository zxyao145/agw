using Agw.Agents.Definitions.Controllers;
using Agw.Auth.Api;
using Agw.Files.Api;
using Agw.Host.Controllers;
using Agw.Host.Hosting;
using Agw.Integrations.Controllers;
using Agw.Jobs.Api;
using Agw.Manager.Api.Controllers;
using Agw.Projects.Controllers;
using Agw.Setup.Controllers;
using Agw.Skills.Controllers;
using Microsoft.AspNetCore.Mvc.ApplicationParts;

namespace Agw.ControlPlane.Host;

public sealed class ControlPlaneHostModule : IAgwHostModule
{
    public void AddApplicationParts(ApplicationPartManager applicationParts)
    {
        AddAssembly(applicationParts, typeof(DashboardController));
        AddAssembly(applicationParts, typeof(AgentsController));
        AddAssembly(applicationParts, typeof(ProjectsController));
        AddAssembly(applicationParts, typeof(FilesController));
        AddAssembly(applicationParts, typeof(SkillsController));
        AddAssembly(applicationParts, typeof(SetupController));
        AddAssembly(applicationParts, typeof(AuthController));
        AddAssembly(applicationParts, typeof(OAuthController));
        AddAssembly(applicationParts, typeof(ToolsController));
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapJobsApi();
        app.MapControllers();
    }

    private static void AddAssembly(ApplicationPartManager applicationParts, Type marker)
    {
        var assembly = marker.Assembly;
        if (applicationParts.ApplicationParts.OfType<AssemblyPart>().All(part => part.Assembly != assembly))
        {
            applicationParts.ApplicationParts.Add(new AssemblyPart(assembly));
        }
    }
}
