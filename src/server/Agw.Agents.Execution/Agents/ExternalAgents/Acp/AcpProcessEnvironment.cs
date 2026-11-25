using System.Collections;

namespace Agw.Agents.Execution.Agents.ExternalAgents.Acp;

internal static class AcpProcessEnvironment
{
    private static readonly HashSet<string> HostKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH",
        "HOME",
        "USER",
        "LOGNAME",
        "SHELL",
        "TMPDIR",
        "TMP",
        "TEMP",
        "TZ",
        "LANG",
        "SystemRoot",
        "ComSpec",
        "PATHEXT",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "NO_PROXY",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "NODE_EXTRA_CA_CERTS",
        "CURL_CA_BUNDLE",
        "REQUESTS_CA_BUNDLE",
        "GIT_SSL_CAINFO",
        "NPM_CONFIG_CAFILE",
        "AWS_CA_BUNDLE",
    };

    public static Dictionary<string, string> Build(
        IDictionary hostEnvironment,
        IReadOnlyDictionary<string, string> launchEnvironment,
        IReadOnlyDictionary<string, string> userEnvironment
    )
    {
        var environment = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
        );
        foreach (DictionaryEntry entry in hostEnvironment)
        {
            if (
                entry is { Key: string key, Value: string value }
                && (HostKeys.Contains(key) || key.StartsWith("LC_", StringComparison.Ordinal))
            )
                environment[key] = value;
        }
        foreach (var (key, value) in launchEnvironment)
            environment[key] = value;
        foreach (var (key, value) in userEnvironment)
            environment[key] = value;
        return environment;
    }
}
