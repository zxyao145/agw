namespace Agw.Skills.Application.Remote;

public interface IRemoteSkillClient
{
    string NormalizeUrl(string? remoteUrl);

    Task<RemoteSkillDefinition> FetchAsync(
        string remoteUrl,
        CancellationToken cancellationToken = default);
}
