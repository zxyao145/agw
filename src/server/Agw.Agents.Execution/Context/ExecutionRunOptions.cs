using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agw.Agents.Execution.Context;

/// <summary>
/// 通过 MAF 运行选项向 Agent 调用链传递执行数据。
/// Carries execution data into the Agent call chain through MAF run options.
/// </summary>
internal static class ExecutionRunOptions
{
    public const string ExecutionKey = "agw.execution";

    /// <summary>
    /// 复制运行选项并注入执行数据；原选项保持不变。
    /// Clones the run options and injects the execution data, leaving the original options unchanged.
    /// </summary>
    public static AgentRunOptions With(AgentRunOptions? options, AgentExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scoped = options?.Clone() ?? new AgentRunOptions();
        scoped.AdditionalProperties = scoped.AdditionalProperties is { } properties
            ? new AdditionalPropertiesDictionary(properties)
            : [];
        scoped.AdditionalProperties[ExecutionKey] = context;
        return scoped;
    }

    public static AgentExecutionContext? Read(AgentRunOptions? options) =>
        options?.AdditionalProperties?.TryGetValue(ExecutionKey, out var value) == true
            ? value as AgentExecutionContext
            : null;

    /// <summary>
    /// 发往模型服务前移除执行数据，避免 Provider 把它当作请求参数。
    /// Removes the execution data before the model request so providers never treat it as a request parameter.
    /// </summary>
    public static ChatOptions? WithoutExecution(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.ContainsKey(ExecutionKey) != true)
            return options;
        var filtered = options.Clone();
        filtered.AdditionalProperties = new AdditionalPropertiesDictionary(options.AdditionalProperties);
        filtered.AdditionalProperties.Remove(ExecutionKey);
        return filtered;
    }
}
