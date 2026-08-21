import type { LegacyLocalConfigV2 } from "./types";

export class ServerProfileValidationError extends Error {
  public constructor(message: string) {
    super(message);
    this.name = "ServerProfileValidationError";
  }
}

export function normalizeServerUrl(value: string): string {
  const trimmed = value.trim();
  if (!trimmed) throw new ServerProfileValidationError("Server URL is required.");

  let parsed: URL;
  try {
    parsed = new URL(trimmed);
  } catch {
    throw new ServerProfileValidationError("Server URL must be an absolute HTTP URL.");
  }

  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    throw new ServerProfileValidationError("Server URL must use HTTP or HTTPS.");
  }
  if (parsed.username || parsed.password) {
    throw new ServerProfileValidationError("Server URL must not include credentials.");
  }
  if (parsed.search || parsed.hash) {
    throw new ServerProfileValidationError("Server URL must not include a query or hash.");
  }
  if (parsed.pathname !== "/") {
    throw new ServerProfileValidationError("Server URL must not include a subpath.");
  }

  return `${parsed.protocol}//${parsed.host}`;
}

export function normalizeProfileName(value: string, serverUrl: string): string {
  const trimmed = value.trim();
  if (trimmed.length > 64) {
    throw new ServerProfileValidationError("Profile name must be 64 characters or fewer.");
  }
  return trimmed || new URL(serverUrl).host;
}

export function normalizeToken(value: string): string {
  const trimmed = value.trim();
  if (!trimmed) throw new ServerProfileValidationError("API token is required.");
  return trimmed;
}

export function parseEncodedConfig(encodedValue: string): LegacyLocalConfigV2 {
  const normalized = encodedValue.trim().replace(/\s/gu, "");
  if (!normalized) throw new ServerProfileValidationError("Base64URL configuration is required.");
  if (!/^[A-Za-z0-9_-]+$/u.test(normalized) || normalized.length % 4 === 1) {
    throw new ServerProfileValidationError("Base64URL configuration is invalid.");
  }

  try {
    const padded = normalized
      .replaceAll("-", "+")
      .replaceAll("_", "/")
      .padEnd(normalized.length + ((4 - (normalized.length % 4)) % 4), "=");
    const binary = atob(padded);
    const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
    return parseLegacyConfig(JSON.parse(new TextDecoder().decode(bytes)));
  } catch (error) {
    if (error instanceof ServerProfileValidationError) throw error;
    throw new ServerProfileValidationError("Base64URL configuration is invalid.");
  }
}

export function parseLegacyConfig(value: unknown): LegacyLocalConfigV2 {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new ServerProfileValidationError("Configuration must be a JSON object.");
  }
  const raw = value as Record<string, unknown>;
  if (raw.version !== 2 || raw.apiMajorVersion !== 1) {
    throw new ServerProfileValidationError("Configuration version is not supported.");
  }
  if (typeof raw.serverUrl !== "string" || typeof raw.token !== "string") {
    throw new ServerProfileValidationError("Configuration is missing serverUrl or token.");
  }
  return {
    version: 2,
    apiMajorVersion: 1,
    serverUrl: normalizeServerUrl(raw.serverUrl),
    token: normalizeToken(raw.token),
  };
}
