namespace Agw.Shared.Data.Entities.Agentflows;

public enum AgentflowEdgeKind
{
    /// <summary>
    /// 普通一对一边。
    /// 运行时调用 AddEdge(source, target, ...)，可以带 ConditionJson 条件，条件满足才走这条边。
    /// </summary>
    Direct = 0,

    /// <summary>
    /// 一对多选择边。
    /// 编译时按同一个 SourceNodeId 分组，通过 AddFanOutEdge selector 选择所有条件命中的目标；无条件目标始终命中。
    /// </summary>
    FanOut = 1,

    /// <summary>
    /// 多对一汇聚屏障。
    /// 编译时按同一个 TargetNodeId 分组。普通图使用 AddFanInBarrierEdge；循环图可缓存一次性 Input，并在环内来源每轮到达后复用。
    /// </summary>
    FanInBarrier = 2,

    /// <summary>
    /// 有序互斥条件分支。
    /// 编译时按同一个 SourceNodeId 分组，并按 ConfigJson.switchCaseOrder 依次添加到 AddSwitch。
    /// </summary>
    SwitchCase = 3,

    /// <summary>
    /// 有序互斥条件分支的默认出口。
    /// 同一个 SourceNodeId 最多存在一条，并编译为 SwitchBuilder.WithDefault。
    /// </summary>
    SwitchDefault = 4,
}
