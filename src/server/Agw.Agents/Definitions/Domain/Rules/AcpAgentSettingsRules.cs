using System.Text.RegularExpressions;
using Agw.Agents.ExternalAgents.Acp;
using Agw.Shared.Exceptions;

namespace Agw.Agents.Definitions.Domain.Rules;

public static partial class AcpAgentSettingsRules
{
    private const int MaxRegistryAgentIdLength = 100;
    private const int MaxCommandLength = 1024;
    private const int MaxArguments = 64;
    private const int MaxArgumentLength = 4096;
    private const int MaxIdLength = 200;
    private const int MaxConfigOptions = 32;
    private const int MaxConfigValueLength = 1000;

    public static void Validate(AcpAgentSettings settings)
    {
        if (settings.Registry is null && settings.Command is null)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                "ACP agents require either registry or command in Extra Settings."
            );
        }

        if (settings.Registry is { } registry)
        {
            if (settings.Command is not null)
                throw Invalid("command", "cannot be combined with registry");
            if (settings.Args is not null)
                throw Invalid("args", "requires command");
            ValidateRegistry(registry);
        }
        else
        {
            ValidateCommand(settings.Command, settings.Args);
        }

        ValidateId("authMethodId", settings.AuthMethodId);
        ValidateId("modeId", settings.ModeId);
        if (settings.ConfigOptions is { } options)
            ValidateConfigOptions(options);
    }

    public static bool IsRegistryAgentId(string? id) =>
        id is { Length: >= 1 and <= MaxRegistryAgentIdLength } && RegistryAgentIdPattern().IsMatch(id);

    public static bool IsRegistryVersion(string version) => RegistryVersionPattern().IsMatch(version);

    private static void ValidateRegistry(AcpRegistryReference registry)
    {
        if (!IsRegistryAgentId(registry.AgentId))
            throw Invalid(
                "registry.agentId",
                $"must start with a lowercase letter and contain up to {MaxRegistryAgentIdLength} "
                    + "lowercase letters, digits or hyphens"
            );
        if (registry.Version is { } version && !IsRegistryVersion(version))
            throw Invalid("registry.version", "must use major.minor.patch");
    }

    private static void ValidateCommand(string? command, IReadOnlyList<string>? arguments)
    {
        if (
            string.IsNullOrWhiteSpace(command)
            || command.Length > MaxCommandLength
            || command.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0
        )
            throw Invalid(
                "command",
                $"must be a nonblank executable name or path of at most {MaxCommandLength} "
                    + "characters without line breaks or NUL"
            );
        if (
            arguments is { } args
            && (
                args.Count > MaxArguments
                || args.Any(arg => arg is null || arg.Length > MaxArgumentLength || arg.Contains('\0'))
            )
        )
            throw Invalid(
                "args",
                $"must contain at most {MaxArguments} strings of at most {MaxArgumentLength} characters without NUL"
            );
    }

    private static void ValidateId(string key, string? value)
    {
        if (value is { Length: < 1 or > MaxIdLength })
            throw Invalid(key, $"must have between 1 and {MaxIdLength} characters");
    }

    private static void ValidateConfigOptions(IReadOnlyDictionary<string, AcpConfigOptionValue> options)
    {
        if (options.Count > MaxConfigOptions)
            throw Invalid("configOptions", $"must contain at most {MaxConfigOptions} entries");
        foreach (var (key, value) in options)
        {
            ValidateId($"configOptions.{key}", key);
            if (
                value
                is not ({ Text.Length: <= MaxConfigValueLength, Boolean: null } or { Text: null, Boolean: not null })
            )
                throw Invalid(
                    $"configOptions.{key}",
                    $"must be a string of at most {MaxConfigValueLength} characters or a boolean"
                );
        }
    }

    private static AgwException Invalid(string key, string detail) =>
        new(ErrorCodes.InvalidParam, $"ACP Extra Settings '{key}' {detail}.");

    [GeneratedRegex(@"\A[a-z][a-z0-9-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryAgentIdPattern();

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex RegistryVersionPattern();
}
