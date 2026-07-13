export type { AgentDto } from "@/types/agentflow";

export interface AgentCreateRequest {
  displayName: string;
  name: string;
  description: string;
  systemPrompt: string;
  modelProviderId: string | null;
  enableSummary: boolean;
  tools: string | null;
  mcpToolServerIds?: string[] | null;
  skillIds?: string[] | null;
  appInstanceIds?: string[] | null;
  environmentVariables: Record<string, string>;
}

export interface AgentUpdateRequest {
  displayName: string;
  description: string;
  systemPrompt: string;
  modelProviderId: string | null;
  enableSummary: boolean;
  tools: string | null;
  mcpToolServerIds?: string[] | null;
  skillIds?: string[] | null;
  appInstanceIds?: string[] | null;
  extra: string | null;
  environmentVariables: Record<string, string>;
}

export type { McpToolServerDto, SkillDto, ToolInfo } from "@/components/definition-capabilities";
export type { ModelProviderDto } from "@/types/agentflow";
