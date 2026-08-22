using Agw.DataPlane.Host;
using Agw.Host;
using Agw.Host.Hosting;

await AgwHostApplication.RunAsync(args, AgwHostProfile.DataPlane, new DataPlaneHostModule());
