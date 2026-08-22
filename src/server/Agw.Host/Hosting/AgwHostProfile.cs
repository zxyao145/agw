using Microsoft.AspNetCore.Mvc.ApplicationParts;

namespace Agw.Host.Hosting;

public enum AgwHostProfile
{
    ControlPlane,
    DataPlane,
    Standalone,
}

public interface IAgwHostModule
{
    void AddApplicationParts(ApplicationPartManager applicationParts);

    void MapEndpoints(WebApplication app);
}
