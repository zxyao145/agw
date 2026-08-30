using Agw.ControlPlane.Host;
using Agw.Host;
using Agw.Host.Hosting;

await AgwHostApplication.RunAsync(args, AgwHostProfile.ControlPlane, new ControlPlaneHostModule());
