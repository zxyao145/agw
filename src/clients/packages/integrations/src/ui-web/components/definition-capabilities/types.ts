export type ToolInfo = {
  name: string;
  description: string;
  category: string;
  typeName: string;
  parameters: Array<{
    name: string;
    type: string;
    description?: string;
    isOptional: boolean;
  }>;
};

export type McpToolServerDto = {
  id: string;
  name: string;
};

export type SkillDto = {
  id: string;
  name: string;
  description: string;
  isBuiltIn?: boolean;
  agentIds: string[];
};

export type ConnectionOption = {
  id: string;
  pluginId: string;
  connectorId: string;
  authSchemeId: string;
  displayName: string;
  alias: string;
  status: string;
  subject?: string | null;
};
