namespace Agw.Agents.Contracts.Execution;

/// <summary>
/// 执行 Agent 的程序种类；数值与持久化的外部 Agent 种类保持一致。
/// The kind of program that executes an Agent; values match the persisted external Agent kind.
/// </summary>
public enum EngineKind
{
    /// <summary>
    /// System Agent：由 MAF 的 ChatClientAgent 直接调用模型。
    /// System Agent: MAF ChatClientAgent calling the model directly.
    /// </summary>
    Maf = 0,
    ClaudeCode = 1,
    Codex = 2,
    Pi = 3,
}
