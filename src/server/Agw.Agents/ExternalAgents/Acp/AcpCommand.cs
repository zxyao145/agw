using Agw.Agents.Application.Acp;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agw.Agents.ExternalAgents.Acp;

public static class AcpCommand
{
    public static async Task<bool> TryRunAsync(string[] args, IConfiguration configuration, AgwDataPaths dataPaths)
    {
        if (args is not ["acp", "install", ..])
            return false;
        try
        {
            var ids = ReadAgentIds(args).ToArray();
            if (ids.Length == 0)
                throw new AgwException(ErrorCodes.InvalidParam, "Usage: acp install <agentId> [<agentId> ...]");
            await using var services = new ServiceCollection()
                .AddLogging(logging =>
                    logging
                        .AddFilter("System.Net.Http.HttpClient", LogLevel.Warning)
                        .AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace)
                )
                .AddSingleton(dataPaths)
                .AddAcpAgentInstallation(configuration)
                .BuildServiceProvider();
            var registry = await services.GetRequiredService<IAcpRegistryClient>().FetchAsync(CancellationToken.None);
            var installer = services.GetRequiredService<IAcpAgentInstaller>();
            foreach (var id in ids)
            {
                var entry =
                    registry.Document.Agents.FirstOrDefault(entry => entry.Id == id)
                    ?? throw new AgwException(ErrorCodes.InvalidParam, $"Unknown ACP registry agent '{id}'.");
                var result = await installer.InstallAsync(entry, CancellationToken.None);
                Console.WriteLine(
                    result.AlreadyInstalled
                        ? $"already installed {id} {result.Version}"
                        : $"installed {id} {result.Version} ({result.Distribution}) at {result.Directory}"
                );
            }
            Environment.ExitCode = 0;
        }
        catch (AgwException error)
        {
            Console.Error.WriteLine(error.Message);
            Environment.ExitCode = 1;
        }
        return true;
    }

    private static IEnumerable<string> ReadAgentIds(string[] args)
    {
        for (var index = 2; index < args.Length; index++)
        {
            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                if (!args[index].Contains('='))
                    index++;
            }
            else
                yield return args[index];
        }
    }
}
