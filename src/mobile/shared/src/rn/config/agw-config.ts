import { decodeUtf8Base64Url, encodeUtf8Base64Url } from "./base64";

export type AgwLocalConfig = {
  version: 1;
  serverDomain: string;
  apiKey: string;
};

export class AgwConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "AgwConfigError";
  }
}

export function createLocalConfig({
  apiKey,
  serverDomain,
}: {
  apiKey: string;
  serverDomain: string;
}): AgwLocalConfig {
  return {
    version: 1,
    serverDomain: normalizeServerDomain(serverDomain),
    apiKey: normalizeApiKey(apiKey),
  };
}

export function parseEncodedConfig(encodedValue: string): AgwLocalConfig {
  try {
    return parseConfigFileContent(decodeUtf8Base64Url(encodedValue));
  } catch (error) {
    if (error instanceof AgwConfigError) {
      throw error;
    }

    throw new AgwConfigError(
      error instanceof Error
        ? error.message
        : "Base64URL configuration is invalid."
    );
  }
}

export function encodeConfigBase64Url(config: AgwLocalConfig): string {
  return encodeUtf8Base64Url(serializeConfig(config));
}

export function parseConfigFileContent(content: string): AgwLocalConfig {
  let rawConfig: unknown;

  try {
    rawConfig = JSON.parse(content);
  } catch {
    throw new AgwConfigError("Configuration JSON is invalid.");
  }

  return normalizeRawConfig(rawConfig);
}

export function serializeConfig(config: AgwLocalConfig): string {
  const normalizedConfig = createLocalConfig(config);

  return JSON.stringify({
    version: normalizedConfig.version,
    serverDomain: normalizedConfig.serverDomain,
    apiKey: normalizedConfig.apiKey,
  });
}

function normalizeRawConfig(rawConfig: unknown): AgwLocalConfig {
  if (!isRecord(rawConfig)) {
    throw new AgwConfigError("Configuration must be a JSON object.");
  }

  const serverDomain =
    readStringProperty(rawConfig, "serverDomain") ??
    readStringProperty(rawConfig, "domain");
  const apiKey =
    readStringProperty(rawConfig, "apiKey") ??
    readStringProperty(rawConfig, "api_key");

  if (serverDomain === undefined) {
    throw new AgwConfigError("Configuration is missing serverDomain.");
  }

  if (apiKey === undefined) {
    throw new AgwConfigError("Configuration is missing apiKey.");
  }

  return createLocalConfig({ apiKey, serverDomain });
}

function normalizeServerDomain(value: string): string {
  const trimmedValue = value.trim();

  if (!trimmedValue) {
    throw new AgwConfigError("Server domain is required.");
  }

  let parsedUrl: URL;

  try {
    parsedUrl = new URL(trimmedValue);
  } catch {
    throw new AgwConfigError("Server domain must be an absolute HTTP URL.");
  }

  if (parsedUrl.protocol !== "http:" && parsedUrl.protocol !== "https:") {
    throw new AgwConfigError("Server domain must use http or https.");
  }

  if (parsedUrl.username || parsedUrl.password) {
    throw new AgwConfigError("Server domain must not include credentials.");
  }

  if (parsedUrl.search || parsedUrl.hash) {
    throw new AgwConfigError("Server domain must not include query or hash.");
  }

  const path =
    parsedUrl.pathname === "/"
      ? ""
      : parsedUrl.pathname.replace(/\/+$/g, "");

  return `${parsedUrl.protocol}//${parsedUrl.host}${path}`;
}

function normalizeApiKey(value: string): string {
  const trimmedValue = value.trim();

  if (!trimmedValue) {
    throw new AgwConfigError("API key is required.");
  }

  return trimmedValue;
}

function readStringProperty(
  source: Record<string, unknown>,
  key: string
): string | undefined {
  const value = source[key];

  if (typeof value === "string") {
    return value;
  }

  return undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
