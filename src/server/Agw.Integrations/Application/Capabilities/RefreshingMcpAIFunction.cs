using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.AI;

namespace Agw.Integrations.Application.Capabilities;

internal sealed class RefreshingMcpAIFunction : AIFunction
{
    private readonly Guid _connectionId;
    private readonly string _description;
    private readonly IConnectionMcpToolInvoker _invoker;
    private readonly JsonElement _jsonSchema;
    private readonly string _name;
    private readonly string _operationName;
    private readonly JsonElement? _returnJsonSchema;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly string _sourceId;
    private readonly MethodInfo? _underlyingMethod;

    public RefreshingMcpAIFunction(
        string name,
        Guid connectionId,
        string sourceId,
        AIFunction sourceFunction,
        IConnectionMcpToolInvoker invoker)
    {
        _name = name;
        _connectionId = connectionId;
        _sourceId = sourceId;
        _operationName = sourceFunction.Name;
        _description = sourceFunction.Description;
        _jsonSchema = sourceFunction.JsonSchema.Clone();
        _returnJsonSchema = sourceFunction.ReturnJsonSchema?.Clone();
        _serializerOptions = sourceFunction.JsonSerializerOptions;
        _underlyingMethod = sourceFunction.UnderlyingMethod;
        _invoker = invoker;
    }

    public override string Name => _name;

    public override string Description => _description;

    public override JsonElement JsonSchema => _jsonSchema;

    public override JsonElement? ReturnJsonSchema => _returnJsonSchema;

    public override MethodInfo? UnderlyingMethod => _underlyingMethod;

    public override JsonSerializerOptions JsonSerializerOptions => _serializerOptions;

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        return _invoker.InvokeAsync(
            _connectionId,
            _sourceId,
            _operationName,
            arguments,
            cancellationToken);
    }
}
