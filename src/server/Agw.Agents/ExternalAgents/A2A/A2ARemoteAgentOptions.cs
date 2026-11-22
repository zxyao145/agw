namespace Agw.Agents.ExternalAgents.A2A;

public sealed class A2ARemoteAgentOptions
{
    public const string SectionName = "A2ARemoteAgents";

    public bool AllowPrivateNetworks { get; set; } = true;
    public TimeSpan CardFetchTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int CardMaxBytes { get; set; } = 1048576;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan StreamIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int ResubscribeAttempts { get; set; } = 3;
}
