
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

export type WorkflowDto = {
  id: string
  name: string
  description: string | null
  pattern: number
  configurationJson: string | null
  enable: boolean
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
}
