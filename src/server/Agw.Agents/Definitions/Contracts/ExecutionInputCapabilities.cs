namespace Agw.Agents.Definitions.Contracts;

/// <summary>
/// 执行目标在 Turn 运行期间接收输入的能力。
/// The ability of an execution target to accept input while a turn runs.
/// </summary>
/// <param name="SupportsSteer">是否支持把输入送达正在运行的 Turn。Whether input can be delivered to the running turn.</param>
/// <param name="Reason">不支持时的原因。The reason when steering is not supported.</param>
public sealed record ExecutionInputCapabilities(bool SupportsSteer, string? Reason = null)
{
    /// <summary>
    /// 按执行引擎判断是否支持 Steer；服务端用同一个判断绑定 Turn 的 Steer 集合，能力查询与实际行为因此一致。
    /// Decides steering support by execution engine; the server binds the turn's Steer inbox with the same decision, so the capability query matches actual behavior.
    /// </summary>
    public static ExecutionInputCapabilities ForEngine(EngineKind engine, ExecutionProvider provider) =>
        engine switch
        {
            EngineKind.Maf => new(true),
            EngineKind.ClaudeCode => new(false, "Claude Code does not accept input while a turn is running."),
            _ when provider == ExecutionProvider.Distributed => new(
                false,
                "The distributed execution provider runs System Agents only."
            ),
            EngineKind.Codex or EngineKind.Pi => new(true),
            _ => new(false, $"The {engine} integration does not accept input while a turn is running."),
        };

    public static ExecutionInputCapabilities ForAgentflow() =>
        new(false, "An Agentflow does not accept input while it is running.");
}
