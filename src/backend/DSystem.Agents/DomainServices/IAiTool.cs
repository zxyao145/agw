using Microsoft.Extensions.AI;

namespace DSystem.Domain.Tools;

/// <summary>
/// Interface for AI tools that can be executed by agents.
/// Implementations can be either synchronous or asynchronous.
/// </summary>
public interface IAiTool
{
    /// <summary>
    /// Gets the unique name of the tool.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the description of what the tool does.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the category of the tool for grouping purposes.
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Converts this tool to an AIFunction for use with Microsoft.Extensions.AI.
    /// </summary>
    /// <returns>An AIFunction instance that can be used by AI agents.</returns>
    AIFunction ToAIFunction();
}

/// <summary>
/// Interface for async AI tools that can be executed by agents.
/// </summary>
public interface IAsyncAiTool : IAiTool
{
    /// <summary>
    /// Executes the tool asynchronously with the given arguments.
    /// </summary>
    /// <param name="arguments">The arguments for the tool execution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the tool execution.</returns>
    Task<object?> ExecuteAsync(AIFunctionArguments arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for sync AI tools that can be executed by agents.
/// </summary>
public interface ISyncAiTool : IAiTool
{
    /// <summary>
    /// Executes the tool synchronously with the given arguments.
    /// </summary>
    /// <param name="arguments">The arguments for the tool execution.</param>
    /// <returns>The result of the tool execution.</returns>
    object? Execute(AIFunctionArguments arguments);
}
