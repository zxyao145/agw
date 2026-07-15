export interface BaseDto {
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
}

export interface AgentDto extends BaseDto {
  id: string;
  displayName: string;
  name: string;
  systemPrompt: string;
  modelProviderId: string | null;
  summaryModelProviderId: string | null;
  enableSummary: boolean;

  description: string;
  tools?: string | null;
  type: number; // 0 = System, 1 = External
  extra?: string | null;
  environmentVariables: Record<string, string>;
  agentMcpToolServers?: Array<{
    agentId: string;
    mcpToolServerId: string;
  }> | null;
  agentSkillRelations?: Array<{
    agentId: string;
    skillId: string;
  }> | null;
  agentConnectionRelations?: Array<{
    agentId: string;
    connectionId: string;
  }> | null;
}

export interface ModelProviderDto {
  id: string;
  modelId: string;
  providerId: string;
  providerName: string;
  providerType: string;
  modelName: string;
}

export const AgentflowNodeKind = {
  Agent: 0,
  WorkflowAsAgent: 1,
  PromptAdapter: 2,
  HumanGate: 3,
  CheckpointMarker: 4,
  ConcurrentBlock: 5,
  HandoffBlock: 6,
  GroupChatBlock: 7,
  MagenticBlock: 8,
  Output: 9,
  Input: 10,
} as const;

export type AgentflowNodeKind = (typeof AgentflowNodeKind)[keyof typeof AgentflowNodeKind];

export const AgentflowEdgeKind = {
  Direct: 0,
  FanOut: 1,
  FanIn: 2,
} as const;

export type AgentflowEdgeKind = (typeof AgentflowEdgeKind)[keyof typeof AgentflowEdgeKind];

export interface AgentflowNodeDto extends BaseDto {
  agentflowId: string;
  nodeId: string;
  kind: AgentflowNodeKind;
  relateId: string | null;
  name: string | null;
  positionJson: string | null;
  instructions: string | null;
  configJson: string | null;
}

export interface AgentflowEdgeDto extends BaseDto {
  agentflowId: string;
  edgeId: string;
  sourceNodeId: string;
  targetNodeId: string;
  kind: AgentflowEdgeKind;
  label: string | null;
  conditionJson: string | null;
  configJson: string | null;
}

export interface AgentflowNodeRequest {
  nodeId: string;
  kind: AgentflowNodeKind;
  relateId: string | null;
  name: string | null;
  positionJson: string | null;
  instructions: string | null;
  configJson: string | null;
}

export interface AgentflowEdgeRequest {
  edgeId: string;
  sourceNodeId: string;
  targetNodeId: string;
  kind: AgentflowEdgeKind;
  label: string | null;
  conditionJson: string | null;
  configJson: string | null;
}

export interface AgentflowSaveRequest {
  name: string;
  description: string | null;
  enable: boolean;
  nodes: AgentflowNodeRequest[];
  edges: AgentflowEdgeRequest[];
  summaryModelProviderId: string | null;
}

export interface AgentflowDto extends BaseDto {
  id: string;
  name: string;
  description: string | null;
  systemPrompt: string;
  enable: boolean;
  summaryModelProviderId: string | null;
}

export interface AgentflowDetailDto extends AgentflowDto {
  nodes: AgentflowNodeDto[];
  edges: AgentflowEdgeDto[];
}
