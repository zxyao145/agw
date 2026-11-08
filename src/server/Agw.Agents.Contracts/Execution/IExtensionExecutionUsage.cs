namespace Agw.Agents.Contracts.Execution;

public interface IExtensionExecutionUsage
{
    Task<bool> HasNonterminalExecutionsAsync(Guid extensionId, CancellationToken cancellationToken = default);
}
