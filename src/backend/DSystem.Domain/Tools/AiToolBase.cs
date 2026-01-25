using Microsoft.Extensions.AI;
using System.Text.Json;

namespace DSystem.Domain.Tools;

/// <summary>
/// Base class for synchronous AI tools with typed parameters.
/// </summary>
/// <typeparam name="TParams">The type representing the tool's parameters.</typeparam>
/// <typeparam name="TResult">The type of the result.</typeparam>
public abstract class AiToolBase<TParams, TResult> : ISyncAiTool
    where TParams : class
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Description { get; }

    /// <inheritdoc/>
    public virtual string Category => "General";

    /// <summary>
    /// Executes the tool with the typed parameters.
    /// </summary>
    /// <param name="parameters">The typed parameters.</param>
    /// <returns>The result of the tool execution.</returns>
    protected abstract TResult Execute(TParams parameters);

    /// <inheritdoc/>
    public object? Execute(AIFunctionArguments arguments)
    {
        var parameters = DeserializeParameters(arguments);
        return Execute(parameters);
    }

    /// <inheritdoc/>
    public virtual AIFunction ToAIFunction()
    {
        return AIFunctionFactory.Create(
            (AIFunctionArguments args) => Execute(args),
            new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = Description
            });
    }

    /// <summary>
    /// Deserializes the arguments to the typed parameters.
    /// </summary>
    protected virtual TParams DeserializeParameters(AIFunctionArguments arguments)
    {
        var json = JsonSerializer.Serialize(arguments);
        return JsonSerializer.Deserialize<TParams>(json)
            ?? throw new InvalidOperationException($"Failed to deserialize parameters for tool {Name}");
    }
}

/// <summary>
/// Base class for asynchronous AI tools with typed parameters.
/// </summary>
/// <typeparam name="TParams">The type representing the tool's parameters.</typeparam>
/// <typeparam name="TResult">The type of the result.</typeparam>
public abstract class AsyncAiToolBase<TParams, TResult> : IAsyncAiTool
    where TParams : class
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Description { get; }

    /// <inheritdoc/>
    public virtual string Category => "General";

    /// <summary>
    /// Executes the tool asynchronously with the typed parameters.
    /// </summary>
    /// <param name="parameters">The typed parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the tool execution.</returns>
    protected abstract Task<TResult> ExecuteAsync(TParams parameters, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public async Task<object?> ExecuteAsync(AIFunctionArguments arguments, CancellationToken cancellationToken = default)
    {
        var parameters = DeserializeParameters(arguments);
        return await ExecuteAsync(parameters, cancellationToken);
    }

    /// <inheritdoc/>
    public virtual AIFunction ToAIFunction()
    {
        return AIFunctionFactory.Create(
            async (AIFunctionArguments args, CancellationToken ct) => await ExecuteAsync(args, ct),
            new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = Description
            });
    }

    /// <summary>
    /// Deserializes the arguments to the typed parameters.
    /// </summary>
    protected virtual TParams DeserializeParameters(AIFunctionArguments arguments)
    {
        var json = JsonSerializer.Serialize(arguments);
        return JsonSerializer.Deserialize<TParams>(json)
            ?? throw new InvalidOperationException($"Failed to deserialize parameters for tool {Name}");
    }
}

/// <summary>
/// Simple base class for synchronous AI tools without complex parameter types.
/// </summary>
public abstract class SimpleAiTool : ISyncAiTool
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Description { get; }

    /// <inheritdoc/>
    public virtual string Category => "General";

    /// <summary>
    /// Executes the tool with raw arguments.
    /// </summary>
    protected abstract object? ExecuteCore(AIFunctionArguments arguments);

    /// <inheritdoc/>
    public object? Execute(AIFunctionArguments arguments) => ExecuteCore(arguments);

    /// <inheritdoc/>
    public abstract AIFunction ToAIFunction();
}

/// <summary>
/// Simple base class for asynchronous AI tools without complex parameter types.
/// </summary>
public abstract class SimpleAsyncAiTool : IAsyncAiTool
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract string Description { get; }

    /// <inheritdoc/>
    public virtual string Category => "General";

    /// <summary>
    /// Executes the tool asynchronously with raw arguments.
    /// </summary>
    protected abstract Task<object?> ExecuteCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public Task<object?> ExecuteAsync(AIFunctionArguments arguments, CancellationToken cancellationToken = default)
        => ExecuteCoreAsync(arguments, cancellationToken);

    /// <inheritdoc/>
    public abstract AIFunction ToAIFunction();
}
