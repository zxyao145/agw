export type McpToolServerDto = {
  id: string;
  name: string;
};

export type SkillDto = {
  id: string;
  name: string;
  description: string;
  kind?: "BuiltIn" | "Local" | "Remote";
  remoteUrl?: string | null;
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
