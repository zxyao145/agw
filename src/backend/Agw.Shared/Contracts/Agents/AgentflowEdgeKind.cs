namespace Agw.Shared.Contracts.Agents;

public enum AgentflowEdgeKind
{
    /// <summary>
    /// 普通一对一边。
    /// 运行时调用 AddEdge(source, target, ...)，可以带 ConditionJson 条件，条件满足才走这条边。
    /// 见 AgentflowWorkflowCompiler.cs:387
    /// </summary>
    Direct = 0,

    /// <summary>
    /// 一对多广播边。
    /// 编译时按同一个 SourceNodeId 分组，把同一个 source 的多个目标合成一次 AddFanOutEdge(source, targets, ...)。语义是一个节点输出同时分发给多个下游。
    /// 见 AgentflowWorkflowCompiler.cs:403
    /// </summary>
    FanOut = 1,

    /// <summary>
    /// 多对一汇聚屏障。
    /// 编译时按同一个 TargetNodeId 分组，把多个 source 合成一次 AddFanInBarrierEdge(sources, target, ...)。语义是多个上游都完成后，目标节点才执行。
    /// 见 AgentflowWorkflowCompiler.cs:413
    /// </summary>
    FanIn = 2,
}
