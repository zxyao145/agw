using Agw.A2A;
using Agw.A2A.Extensions;
using Agw.Agents.Execution.Transport.SignalR;
using Agw.Host.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Options;

namespace Agw.DataPlane.Host;

public sealed class DataPlaneHostModule : IAgwHostModule
{
    public void AddApplicationParts(ApplicationPartManager applicationParts) { }

    public void MapEndpoints(WebApplication app)
    {
        var a2AOptions = app.Services.GetRequiredService<IOptions<AgwA2AServerOptions>>().Value;
        app.MapAgwA2A(a2AOptions.Prefix).RequireAuthorization();
        app.MapHub<ExecutionHub>(
                "/api/hubs/exec",
                options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                }
            )
            .RequireAuthorization();
    }
}
