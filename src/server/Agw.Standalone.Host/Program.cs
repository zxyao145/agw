using Agw.ControlPlane.Host;
using Agw.DataPlane.Host;
using Agw.Host;
using Agw.Host.Hosting;

await AgwHostApplication.RunAsync(
    args,
    AgwHostProfile.Standalone,
    new ControlPlaneHostModule(),
    new DataPlaneHostModule()
);
