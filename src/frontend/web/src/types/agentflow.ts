
export type AgentDto = {
  id: string
  name: string
  instructions: string
  systemPrompt: string
  modelProviderApiKeyId: string
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
}

export enum AgentflowNodeType {
  AgentNode = 0,
  AgentflowNode = 1,
}

export type AgentflowNodeDto = {
  agentflowId: string
  nodeId: string
  type: AgentflowNodeType
  relateId: string
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
}

export type AgentflowEdgeDto = {
  agentflowId: string
  edgeId: string
  sourceNodeId: string
  targetNodeId: string
  animated: boolean
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
}

export type AgentflowDto = {
  id: string
  name: string
  description: string | null
  systemPrompt: string
  pattern: number
  configurationJson: string | null
  enable: boolean
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
}