namespace Agw.Agents.Execution.Agents.Skills;

public interface IRemoteSkillClient
{
    string NormalizeUrl(string? remoteUrl);

    Task<RemoteSkillDefinition> FetchAsync(
        string remoteUrl,
        CancellationToken cancellationToken = default);
}
