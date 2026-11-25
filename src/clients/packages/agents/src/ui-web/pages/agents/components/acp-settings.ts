import { AGENT_EXTRA_SETTINGS_ERROR } from "./agent-extra-settings";
import { AgentType, ExternalAgentKind } from "./types";

const ACP_SETTINGS_LIMITS = {
  registryAgentIdLength: 100,
  commandLength: 1024,
  argumentCount: 64,
  argumentLength: 4096,
  identifierLength: 200,
  configOptionCount: 32,
  configOptionValueLength: 1000,
} as const;

const settingsKeys = new Set([
  "registry",
  "command",
  "args",
  "authMethodId",
  "modeId",
  "configOptions",
]);

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function parseAcpExtraObject(extra: string): Record<string, unknown> | null {
  try {
    const parsed: unknown = JSON.parse(extra);
    return isObject(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

export function isAcpAgent(agentType: number, kind: number): boolean {
  return agentType === AgentType.External && kind === ExternalAgentKind.ACP;
}

export function readAcpRegistryAgentId(extra: string): string | null {
  const registry = parseAcpExtraObject(extra)?.registry;
  return isObject(registry) && typeof registry.agentId === "string" ? registry.agentId : null;
}

export function withAcpRegistryAgent(extra: string, agentId: string): string {
  const settings = parseAcpExtraObject(extra);
  if (!settings) throw new Error(AGENT_EXTRA_SETTINGS_ERROR);

  delete settings.command;
  delete settings.args;
  settings.registry = { agentId };
  return JSON.stringify(settings);
}

export function getAcpSettingsError(extra: string): string | null {
  const settings = parseAcpExtraObject(extra.trim() || "{}");
  if (!settings) return AGENT_EXTRA_SETTINGS_ERROR;

  const unknownKey = Object.keys(settings).find((key) => !settingsKeys.has(key));
  if (unknownKey !== undefined) return `Unknown ACP Extra Settings key '${unknownKey}'.`;

  const { registry, command, args, authMethodId, modeId, configOptions } = settings;
  if (registry == null && command == null) {
    return "Select an agent from the ACP registry or set command.";
  }

  if (registry != null) {
    if (command != null) return "ACP Extra Settings cannot combine registry and command.";
    if (args != null) return "ACP Extra Settings require command when args is set.";
    if (
      !isObject(registry) ||
      Object.keys(registry).some((key) => key !== "agentId" && key !== "version")
    ) {
      return "ACP Extra Settings registry must be an object with agentId and optional version.";
    }
    if (
      typeof registry.agentId !== "string" ||
      registry.agentId.length > ACP_SETTINGS_LIMITS.registryAgentIdLength ||
      !/^[a-z][a-z0-9-]*$/.test(registry.agentId)
    ) {
      return `ACP Extra Settings registry.agentId must start with a lowercase letter and contain up to ${ACP_SETTINGS_LIMITS.registryAgentIdLength} lowercase letters, digits or hyphens.`;
    }
    if (
      registry.version != null &&
      (typeof registry.version !== "string" || !/^[0-9]+\.[0-9]+\.[0-9]+$/.test(registry.version))
    ) {
      return "ACP Extra Settings registry.version must use major.minor.patch.";
    }
  } else if (
    typeof command !== "string" ||
    !command.trim() ||
    command.length > ACP_SETTINGS_LIMITS.commandLength ||
    /[\r\n\0]/.test(command)
  ) {
    return `ACP Extra Settings command must be a nonblank executable name or path of at most ${ACP_SETTINGS_LIMITS.commandLength} characters without line breaks or NUL.`;
  }

  if (
    args != null &&
    (!Array.isArray(args) ||
      args.length > ACP_SETTINGS_LIMITS.argumentCount ||
      args.some(
        (arg) =>
          typeof arg !== "string" ||
          arg.length > ACP_SETTINGS_LIMITS.argumentLength ||
          arg.includes("\0"),
      ))
  ) {
    return `ACP Extra Settings args must contain at most ${ACP_SETTINGS_LIMITS.argumentCount} strings of at most ${ACP_SETTINGS_LIMITS.argumentLength} characters without NUL.`;
  }

  for (const [key, value] of [
    ["authMethodId", authMethodId],
    ["modeId", modeId],
  ] as const) {
    if (
      value != null &&
      (typeof value !== "string" ||
        value.length === 0 ||
        value.length > ACP_SETTINGS_LIMITS.identifierLength)
    ) {
      return `ACP Extra Settings ${key} must be a string of 1 to ${ACP_SETTINGS_LIMITS.identifierLength} characters.`;
    }
  }

  if (configOptions != null) {
    if (
      !isObject(configOptions) ||
      Object.keys(configOptions).length > ACP_SETTINGS_LIMITS.configOptionCount
    ) {
      return `ACP Extra Settings configOptions must be an object with at most ${ACP_SETTINGS_LIMITS.configOptionCount} entries.`;
    }
    for (const [key, value] of Object.entries(configOptions)) {
      if (key.length === 0 || key.length > ACP_SETTINGS_LIMITS.identifierLength) {
        return `ACP Extra Settings configOptions keys must contain 1 to ${ACP_SETTINGS_LIMITS.identifierLength} characters.`;
      }
      if (
        typeof value !== "boolean" &&
        (typeof value !== "string" || value.length > ACP_SETTINGS_LIMITS.configOptionValueLength)
      ) {
        return `ACP Extra Settings configOptions.${key} must be a string of at most ${ACP_SETTINGS_LIMITS.configOptionValueLength} characters or a boolean.`;
      }
    }
  }

  return null;
}
