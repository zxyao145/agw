import { decodeUtf8Base64Url, encodeUtf8Base64Url } from "./base64";

export type AgwLocalConfig = {
  version: 2;
  apiMajorVersion: 1;
  serverUrl: string;
  token: string;
};

export class AgwConfigError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "AgwConfigError";
  }
}

export function createLocalConfig({
  token,
  serverUrl,
}: {
  token: string;
  serverUrl: string;
}): AgwLocalConfig {
  return {
    version: 2,
    apiMajorVersion: 1,
    serverUrl: normalizeServerUrl(serverUrl),
    token: normalizeToken(token),
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
      error instanceof Error ? error.message : "Base64URL configuration is invalid.",
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
    apiMajorVersion: normalizedConfig.apiMajorVersion,
    serverUrl: normalizedConfig.serverUrl,
    token: normalizedConfig.token,
  });
}

function normalizeRawConfig(rawConfig: unknown): AgwLocalConfig {
  if (!isRecord(rawConfig)) {
    throw new AgwConfigError("Configuration must be a JSON object.");
  }

  if (rawConfig.version !== 2 || rawConfig.apiMajorVersion !== 1) {
    throw new AgwConfigError("Configuration version is not supported.");
  }

  const serverUrl = readStringProperty(rawConfig, "serverUrl");
  const token = readStringProperty(rawConfig, "token");

  if (serverUrl === undefined) {
    throw new AgwConfigError("Configuration is missing serverUrl.");
  }

  if (token === undefined) {
    throw new AgwConfigError("Configuration is missing token.");
  }

  return createLocalConfig({ token, serverUrl });
}

function normalizeServerUrl(value: string): string {
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

  if (parsedUrl.pathname !== "/") {
    throw new AgwConfigError("Server URL must not include a subpath.");
  }

  return `${parsedUrl.protocol}//${parsedUrl.host}`;
}

function normalizeToken(value: string): string {
  const trimmedValue = value.trim();

  if (!trimmedValue) {
    throw new AgwConfigError("API token is required.");
  }

  return trimmedValue;
}

function readStringProperty(source: Record<string, unknown>, key: string): string | undefined {
  const value = source[key];

  if (typeof value === "string") {
    return value;
  }

  return undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
