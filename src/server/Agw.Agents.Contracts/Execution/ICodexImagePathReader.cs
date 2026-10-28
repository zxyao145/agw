namespace Agw.Agents.Contracts.Execution;

public interface ICodexImagePathReader
{
    Task<IReadOnlyDictionary<string, string>> ReadAsync(
        Guid? agentId,
        string threadId,
        string turnId,
        IReadOnlyList<string> itemIds,
        CancellationToken cancellationToken
    );
}
