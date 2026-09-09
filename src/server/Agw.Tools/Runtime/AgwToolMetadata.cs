using System.Runtime.CompilerServices;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.AI;

namespace Agw.Tools.Runtime;

public sealed record AgwToolMetadata(string Source, AgwToolPermission RequiredPermission, bool AllowInPlanMode = false);

public static class AgwToolMetadataBinding
{
    private static readonly ConditionalWeakTable<AITool, AgwToolMetadata> _nonFunctionMetadata = new();

    public static AITool Bind(AITool tool, AgwToolMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(metadata.Source) || !Enum.IsDefined(metadata.RequiredPermission))
        {
            throw new AgwException(ErrorCodes.InvalidParam, $"Tool '{tool.Name}' has invalid Agw metadata.");
        }

        if (tool is not AIFunction function)
        {
            if (RequiresApproval(metadata.RequiredPermission))
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Tool '{tool.Name}' requires '{metadata.RequiredPermission}' permission but is not an AIFunction."
                );
            }

            var boundMetadata = _nonFunctionMetadata.GetValue(tool, _ => metadata);
            if (boundMetadata != metadata)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Tool '{tool.Name}' already has conflicting Agw metadata."
                );
            }

            return tool;
        }

        if (function.GetService<AgwToolMetadata>() is { } existing)
        {
            if (existing != metadata)
            {
                throw new AgwException(
                    ErrorCodes.InvalidParam,
                    $"Tool '{tool.Name}' already has conflicting Agw metadata."
                );
            }

            return RequiresApproval(metadata.RequiredPermission) && function is not ApprovalRequiredAIFunction
                ? new ApprovalRequiredAIFunction(function)
                : tool;
        }

        AIFunction metadataFunction = new MetadataAIFunction(function, metadata);
        if (RequiresApproval(metadata.RequiredPermission))
        {
            return new ApprovalRequiredAIFunction(metadataFunction);
        }

        return metadataFunction;
    }

    public static bool RequiresApproval(AgwToolPermission permission) =>
        permission is AgwToolPermission.Write or AgwToolPermission.Execute;

    public static AgwToolMetadata? GetMetadata(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool is AIFunction function ? function.GetService<AgwToolMetadata>()
            : _nonFunctionMetadata.TryGetValue(tool, out var metadata) ? metadata
            : null;
    }

    private sealed class MetadataAIFunction : DelegatingAIFunction
    {
        private readonly AgwToolMetadata _metadata;

        public MetadataAIFunction(AIFunction innerFunction, AgwToolMetadata metadata)
            : base(innerFunction)
        {
            _metadata = metadata;
        }

        public override object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey == null && serviceType == typeof(AgwToolMetadata)
                ? _metadata
                : base.GetService(serviceType, serviceKey);
    }
}
