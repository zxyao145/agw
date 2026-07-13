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
  agentIds: string[];
};

export type AppInstanceOption = {
  id: string;
  appName: string;
  displayName: string;
  provider: string;
  clientId: string;
  isAuthorized: boolean;
  isAuthorizationExpired: boolean;
  authorizationSubject?: string | null;
};
