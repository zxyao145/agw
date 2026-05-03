import type { ChatTargetType, EnvVar } from "../types";

export type ChatUrlSettings = {
  agentType: 0 | 1 | null;
  agentId: string | null;
  chatSettings?: ChatUrlChatSettings;
};

export type ChatUrlChatSettings = {
  workspace?: string;
  envVars?: EnvVar[];
  extraSettingText?: string;
};

type ChatUrlSettingsSourceTarget = {
  id: string;
  type: ChatTargetType;
};

type BuildChatUrlSettingsInput = {
  target: ChatUrlSettingsSourceTarget | null;
  workspace: string;
  envVars: EnvVar[];
  extraSettingText: string;
};

function encodeBase64Url(value: string): string {
  const bytes = new TextEncoder().encode(value);
  let binary = "";

  bytes.forEach((byte) => {
    binary += String.fromCharCode(byte);
  });

  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}

function decodeBase64Url(value: string): string | null {
  if (!/^[A-Za-z0-9_-]+$/.test(value) || value.length % 4 === 1) {
    return null;
  }

  try {
    const base64 = value.replaceAll("-", "+").replaceAll("_", "/");
    const paddedBase64 = base64.padEnd(Math.ceil(base64.length / 4) * 4, "=");
    const binary = atob(paddedBase64);
    const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));

    return new TextDecoder().decode(bytes);
  } catch {
    return null;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function parseEnvVars(value: unknown): EnvVar[] | null | undefined {
  if (value === undefined) {
    return undefined;
  }

  if (!Array.isArray(value)) {
    return null;
  }

  const envVars: EnvVar[] = [];

  for (const item of value) {
    if (!isRecord(item) || typeof item.key !== "string" || typeof item.value !== "string") {
      return null;
    }

    envVars.push({ key: item.key, value: item.value });
  }

  return envVars;
}

function parseChatSettings(value: unknown): ChatUrlChatSettings | null | undefined {
  if (value === undefined) {
    return undefined;
  }

  if (!isRecord(value)) {
    return null;
  }

  const { workspace, envVars, extraSettingText } = value;
  if (workspace !== undefined && typeof workspace !== "string") {
    return null;
  }

  const parsedEnvVars = parseEnvVars(envVars);
  if (parsedEnvVars === null) {
    return null;
  }

  if (extraSettingText !== undefined && typeof extraSettingText !== "string") {
    return null;
  }

  return {
    ...(workspace === undefined ? {} : { workspace }),
    ...(envVars === undefined ? {} : { envVars: parsedEnvVars }),
    ...(extraSettingText === undefined ? {} : { extraSettingText }),
  };
}

export function buildChatUrlSettings({
  target,
  workspace,
  envVars,
  extraSettingText,
}: BuildChatUrlSettingsInput): ChatUrlSettings {
  return {
    agentType: target ? (target.type === "agent" ? 0 : 1) : null,
    agentId: target?.id ?? null,
    chatSettings: {
      workspace,
      envVars,
      extraSettingText,
    },
  };
}

export function encodeChatUrlSettings(settings: unknown): string {
  return encodeBase64Url(JSON.stringify(settings));
}

export function getChatSettingsHash(settings: string | null): string {
  return settings ? `#settings=${settings}` : "";
}

export function getChatSettingsHashValue(hash: string): string | null {
  if (!hash.startsWith("#")) {
    return null;
  }

  const params = new URLSearchParams(hash.slice(1));
  return params.get("settings");
}

export function decodeChatUrlSettings(value: string | null): ChatUrlSettings | null {
  if (!value) {
    return null;
  }

  const decoded = decodeBase64Url(value);
  if (!decoded) {
    return null;
  }

  try {
    const parsed = JSON.parse(decoded) as unknown;
    if (!isRecord(parsed)) {
      return null;
    }

    const { agentType, agentId, chatSettings, workspace, envVars, extraSettingText } = parsed;
    if (agentType !== 0 && agentType !== 1 && agentType !== null) {
      return null;
    }

    if (agentType === null && agentId !== null) {
      return null;
    }

    if (agentType !== null && (typeof agentId !== "string" || agentId.trim().length === 0)) {
      return null;
    }

    if (workspace !== undefined || envVars !== undefined || extraSettingText !== undefined) {
      return null;
    }

    const parsedChatSettings = parseChatSettings(chatSettings);
    if (parsedChatSettings === null) {
      return null;
    }

    return {
      agentType,
      agentId: typeof agentId === "string" ? agentId.trim() : null,
      ...(chatSettings === undefined ? {} : { chatSettings: parsedChatSettings }),
    };
  } catch {
    return null;
  }
}

export function getTargetValueFromChatUrlSettings(settings: ChatUrlSettings | null): string | null {
  if (!settings || !settings.agentId) {
    return null;
  }

  if (settings.agentType === 0) {
    return `agent:${settings.agentId}`;
  }

  if (settings.agentType === 1) {
    return `agentflow:${settings.agentId}`;
  }

  return null;
}
