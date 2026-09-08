using Agw.Shared.Exceptions;
using Agw.Shared.Utils;

namespace Agw.Agents.Execution.Durable;

/// <summary>
/// 统一 durable payload 的序列化约定，并把空反序列化结果转换为领域冲突。
/// </summary>
internal static class DurableExecutionJson
{
    /// <summary>
    /// 使用仓库统一 JSON 选项序列化 durable payload。
    /// </summary>
    public static string Serialize<T>(T value) => JsonUtil.Serialize(value);

    /// <summary>
    /// 反序列化必需的 durable payload；空结果转换为执行冲突。
    /// </summary>
    public static T DeserializeRequired<T>(string json, string description) =>
        JsonUtil.Deserialize<T>(json)
        ?? throw new AgwException(ErrorCodes.DurableExecutionConflict, $"Stored {description} is invalid.");
}
