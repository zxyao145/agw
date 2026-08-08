export type ToolName =
  | "ask_user_question"
  | "bash"
  | "diff"
  | "generate_guid"
  | "git_clone"
  | "powershell"
  | "run_shell"
  | "web_fetch"
  | "web_search";

export type ToolBlockName =
  | "todo"
  | "mode"
  | "project-memory"
  | "file-access"
  | "background-agents";

export type EmptyToolOptions = Record<string, never>;

export type ToolDefinition = {
  name: ToolName;
  options: EmptyToolOptions;
};

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

export type ToolInfo = {
  kind: "tool" | "toolBlock";
  name: string;
  displayName: string;
  description: string;
  category: string;
  typeName: string;
  memberToolNames: string[];
  scopes: number;
  requiresWorkspace: boolean;
  parameters: Array<{
    name: string;
    type: string;
    description?: string;
    isOptional: boolean;
  }>;
  isAsync: boolean;
  requiresConfirmation: boolean;
  timeoutMs: number;
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

export function createToolBlockValue(name: ToolBlockName): ToolBlockValue {
  const definition: ToolBlockDefinition =
    name === "project-memory"
      ? { name, options: { storage: "database" } }
      : name === "background-agents"
        ? { name, options: { allowedAgentIds: [] } }
        : { name, options: {} };

  return {
    kind: "toolBlock",
    definition,
  };
}
