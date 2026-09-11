using System.Text.Json;
using Agw.Tools.Abstractions.Generated;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.Generated;

internal sealed class GeneratedAgwFunction : AIFunction
{
    private readonly AgwGeneratedToolDescriptor _descriptor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Guid _projectId;
    private readonly JsonElement _jsonSchema;
    private readonly JsonElement? _returnJsonSchema;

    public GeneratedAgwFunction(
        AgwGeneratedToolDescriptor descriptor,
        IServiceScopeFactory scopeFactory,
        Guid projectId
    )
    {
        _descriptor = descriptor;
        _scopeFactory = scopeFactory;
        _projectId = projectId;
        _jsonSchema = JsonDocument.Parse(descriptor.JsonSchema).RootElement.Clone();
        _returnJsonSchema =
            descriptor.ReturnJsonSchema == null
                ? null
                : JsonDocument.Parse(descriptor.ReturnJsonSchema).RootElement.Clone();
    }

    public override string Name => _descriptor.Name;

    public override string Description => _descriptor.Description;

    public override JsonElement JsonSchema => _jsonSchema;

    public override JsonElement? ReturnJsonSchema => _returnJsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IAgwToolInvocationContextInitializer? initializer =
            scope.ServiceProvider.GetService<IAgwToolInvocationContextInitializer>();
        initializer?.Initialize(_projectId);
        var context = new GeneratedToolBindingContext(_projectId, scope.ServiceProvider, arguments);
        object? result = await _descriptor.InvokeAsync(context, cancellationToken).ConfigureAwait(false);
        if (result == null || result is AIContent || result is IEnumerable<AIContent>)
        {
            return result;
        }

        return JsonSerializer.SerializeToElement(result, result.GetType(), AIJsonUtilities.DefaultOptions);
    }
}
