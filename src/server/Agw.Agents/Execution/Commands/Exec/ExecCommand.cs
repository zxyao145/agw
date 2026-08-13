using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

using Agw.Agents.Execution.Commands.Abstracts;
using Agw.Shared.AgwMsgVm;
using Agw.Shared.Data;

namespace Agw.Agents.Execution.Commands.Exec;

public class ExecCommand : AgentRunCommand
{
    [JsonConstructor]
    [SetsRequiredMembers]
    public ExecCommand(
        AgentRuntimeType agentType,
        AgwUserInput input)
    {
        AgentType = agentType;
        Input = input;
    }

    public AgentRuntimeType AgentType { get; set; }

    public Guid? AgentId { get; set; }

    /// <summary>
    /// 获取或设置客户端生成的稳定执行标识，用于 durable 启动幂等和断线恢复。
    /// </summary>
    public Guid? ExecutionId { get; set; }

    public bool Stream { get; set; } = true;

    public AgwUserInput Input { get; set; }
}
