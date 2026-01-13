export interface BaseDto {
  createBy?: string | null;
  createTime?: string | null;
  updateBy?: string | null;
  updateTime?: string | null;
}

export interface AgentDto extends BaseDto {
  id: string;
  name: string;
  systemPrompt: string;
  modelProviderApiKeyId: string;
}

export const AgentflowNodeType = {
  AgentNode: 0,
  AgentflowNode: 1,
} as const;

export type AgentflowNodeType = (typeof AgentflowNodeType)[keyof typeof AgentflowNodeType];

export interface AgentflowNodeDto extends BaseDto {
  agentflowId: string;
  nodeId: string;
  type: AgentflowNodeType;
  relateId: string;
}

export interface AgentflowEdgeDto extends BaseDto {
  agentflowId: string;
  edgeId: string;
  sourceNodeId: string;
  targetNodeId: string;
  animated: boolean;
}

export interface AgentflowDto extends BaseDto {
  id: string;
  name: string;
  description: string | null;
  pattern: number;
  configurationJson: string | null;
  enable: boolean;
}

export interface AgentflowDetailDto extends AgentflowDto {
  nodes: AgentflowNodeDto[];
  edges: AgentflowEdgeDto[];
}
