namespace Agw.Agents.Execution.Agents.AIContextProviders.AgwWorkspace;

/// <summary>
/// Provides additional instructions for an agent invocation.
/// </summary>
public interface IAgentInstructionsSource
{
    /// <summary>
    /// Loads instructions for the current invocation. Null or whitespace results are ignored.
    /// </summary>
    /// <remarks>
    /// Implementations that read external data are responsible for validating it before returning it.
    /// </remarks>
    ValueTask<string?> GetInstructionsAsync(
        AgwInstructionsSourceContext context,
        CancellationToken cancellationToken = default
    );
}
