using System.Collections;

namespace PiAgentSdk.Internal;

internal static class PiProcessEnvironment
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
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
        "http_proxy",
        "https_proxy",
        "all_proxy",
        "no_proxy",
        "SSL_CERT_FILE",
        "SSL_CERT_DIR",
        "NODE_EXTRA_CA_CERTS",
        "CURL_CA_BUNDLE",
        "REQUESTS_CA_BUNDLE",
        "GIT_SSL_CAINFO",
        "NPM_CONFIG_CAFILE",
        "AWS_CA_BUNDLE",
    };

    public static Dictionary<string, string> Build(PiAgentOptions agentOptions, PiSessionOptions sessionOptions) =>
        Build(agentOptions, sessionOptions, Environment.GetEnvironmentVariables());

    internal static Dictionary<string, string> Build(
        PiAgentOptions agentOptions,
        PiSessionOptions sessionOptions,
        IDictionary hostEnvironment
    )
    {
        var result = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
        );
        foreach (DictionaryEntry entry in hostEnvironment)
        {
            if (
                entry.Key is string key
                && entry.Value is string value
                && (AllowedKeys.Contains(key) || key.StartsWith("LC_", StringComparison.Ordinal))
            )
            {
                result[key] = value;
            }
        }

        Overlay(result, agentOptions.EnvironmentVariables);
        Overlay(result, sessionOptions.EnvironmentVariables);
        return result;
    }

    private static void Overlay(IDictionary<string, string> target, IReadOnlyDictionary<string, string>? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            target[key] = value;
        }
    }
}
