using System.Text.Json;
using Agw.Tools.Abstractions;
using Microsoft.Extensions.AI;

namespace Agw.Extensions.Sdk;

public interface IExtensionTool
{
    string Name { get; }

    AgwToolPermission RequiredPermission { get; }

    bool AllowInPlanMode { get; }

    /// <summary>Creates a function with generation-owned JSON options. 使用 generation 自有 JSON 选项创建函数。</summary>
    AIFunction CreateFunction(JsonSerializerOptions serializerOptions);
}
