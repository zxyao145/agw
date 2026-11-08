export type ToolName =
  | "ask_user_question"
  | "diff"
  | "git_clone"
  | "run_shell"
  | "web_fetch"
  | "web_search";

export type ExtensionToolDefinition = {
  name: "extension";
  extensionId: string;
  toolId: string;
};

export type ToolBlockName =
  | "todo"
  | "mode"
  | "project-memory"
  | "user-memory"
  | "file-readonly-access"
  | "file-access"
  | "background-agents";

export type EmptyToolOptions = Record<string, never>;

export type BuiltInToolDefinition = {
  name: ToolName;
  options: EmptyToolOptions;
};

export type ToolDefinition = BuiltInToolDefinition | ExtensionToolDefinition;

type ParameterlessToolBlockDefinition = {
  name: Exclude<ToolBlockName, "project-memory" | "background-agents">;
  options: EmptyToolOptions;
};

export type ProjectMemoryToolBlockDefinition = {
  name: "project-memory";
  options: {
    storage: "database" | "filesystem";
  };
};

export type BackgroundAgentsToolBlockDefinition = {
  name: "background-agents";
  options: {
    allowedAgentIds: string[];
  };
};

export type ToolBlockDefinition =
  | ParameterlessToolBlockDefinition
  | ProjectMemoryToolBlockDefinition
  | BackgroundAgentsToolBlockDefinition;

export type ToolValue = {
  kind: "tool";
  definition: ToolDefinition;
};

export type ToolBlockValue = {
  kind: "toolBlock";
  definition: ToolBlockDefinition;
};

export type ToolValueObject = ToolValue | ToolBlockValue;

export type ToolLiteInfo = {
  kind: "tool" | "toolBlock";
  name: string;
  displayName: string;
  description: string;
  category: string;
  memberToolNames: string[];
  scopes: number;
  requiresWorkspace: boolean;
  requiredPermission?: "none" | "readOnly" | "write" | "execute" | null;
  requiresConfirmation: boolean;
  allowInPlanMode?: boolean;
  extensionId?: string | null;
  extensionToolId?: string | null;
  inputSchema?: unknown;
};

export function parseToolValues(value: ToolValueObject[] | null | undefined): ToolValueObject[] {
  return value == null ? [] : [...value];
}

export function createToolValue(name: ToolName): ToolValue {
  return {
    kind: "tool",
    definition: {
      name,
      options: {},
    },
  };
}

export function getToolCatalogSelectionKey(tool: ToolLiteInfo): string {
  return tool.extensionId && tool.extensionToolId
    ? `extension:${tool.extensionId}:${tool.extensionToolId}`
    : tool.name;
}

export function getToolValueSelectionKey(value: ToolValue): string {
  return value.definition.name === "extension"
    ? `extension:${value.definition.extensionId}:${value.definition.toolId}`
    : value.definition.name;
}

export function createToolValueFromInfo(tool: ToolLiteInfo): ToolValue {
  if (tool.extensionId && tool.extensionToolId) {
    return {
      kind: "tool",
      definition: {
        name: "extension",
        extensionId: tool.extensionId,
        toolId: tool.extensionToolId,
      },
    };
  }

  return createToolValue(tool.name as ToolName);
}

export function createToolBlockValue(name: ToolBlockName): ToolBlockValue {
  const definition: ToolBlockDefinition =
    name === "project-memory"
      ? { name, options: { storage: "filesystem" } }
      : name === "background-agents"
        ? { name, options: { allowedAgentIds: [] } }
        : { name, options: {} };

  return {
    kind: "toolBlock",
    definition,
  };
}
