namespace Agw.Shared.Contracts.Agents;

public enum AgentflowNodeKind
{
    Agent = 0,
    WorkflowAsAgent = 1,
    PromptAdapter = 2,
    HumanGate = 3,
    CheckpointMarker = 4,
    ConcurrentBlock = 5,
    HandoffBlock = 6,
    GroupChatBlock = 7,
    MagenticBlock = 8,
    Output = 9,
}
