using System.Text.Json;
using Agw.Shared.Exceptions;
using Agw.Tools.Abstractions.Generated;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Generated;

internal sealed class GeneratedToolBindingContext : IAgwToolBindingContext
{
    public GeneratedToolBindingContext(Guid projectId, IServiceProvider services, AIFunctionArguments arguments)
    {
        ProjectId = projectId;
        Services = services;
        Arguments = arguments;
    }

    public Guid ProjectId { get; }

    public IServiceProvider Services { get; }

    public AIFunctionArguments Arguments { get; }

    public T GetRequiredArgument<T>(string name)
    {
        if (!Arguments.TryGetValue(name, out object? value))
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Required Tool argument '{name}' was not supplied.");
        }

        return ConvertArgument<T>(name, value);
    }

    public T GetOptionalArgument<T>(string name, T defaultValue) =>
        Arguments.TryGetValue(name, out object? value) ? ConvertArgument<T>(name, value) : defaultValue;

    public T GetRequiredService<T>()
        where T : notnull => Services.GetRequiredService<T>();

    private static T ConvertArgument<T>(string name, object? value)
    {
        if (value is T typed)
        {
            return typed;
        }

        try
        {
            if (value is JsonElement element)
            {
                return element.Deserialize<T>(AIJsonUtilities.DefaultOptions)!;
            }

            JsonElement serialized = JsonSerializer.SerializeToElement(value, AIJsonUtilities.DefaultOptions);
            return serialized.Deserialize<T>(AIJsonUtilities.DefaultOptions)!;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Tool argument '{name}' could not be converted to '{typeof(T).Name}'.",
                exception
            );
        }
    }
}
