namespace Agw.Agents.ExternalAgents.Acp;

public sealed record AcpRegistryDocument(string Version, IReadOnlyList<AcpRegistryAgent> Agents);

public sealed record AcpRegistryAgent(
    string Id,
    string Name,
    string Version,
    string Description,
    string? Repository,
    string? Website,
    string? License,
    AcpRegistryDistribution Distribution,
    IReadOnlyList<string>? Authors = null
);

public static class AcpDistributionKinds
{
    public const string Binary = "binary";
    public const string Npx = "npx";
    public const string Uvx = "uvx";
}

public sealed record AcpDistributionHost(string? Platform, bool HasNpm, bool HasUv, bool IsWindows);

public sealed record AcpRegistryDistribution(
    IReadOnlyDictionary<string, AcpBinaryTarget>? Binary,
    AcpPackageDistribution? Npx,
    AcpPackageDistribution? Uvx
);

public sealed record AcpBinaryTarget(
    Uri Archive,
    string Cmd,
    IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env,
    string? Sha256
);

public sealed record AcpPackageDistribution(
    string Package,
    IReadOnlyList<string>? Args,
    IReadOnlyDictionary<string, string>? Env
);

public sealed record AcpDistributionSelection(
    string? Kind,
    AcpBinaryTarget? Binary,
    AcpPackageDistribution? Package,
    IReadOnlyList<string> Reasons
);
