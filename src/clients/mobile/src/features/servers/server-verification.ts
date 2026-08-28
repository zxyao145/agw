import { ApiError, ApiTransportError, createBearerApiClient } from "@agw/api";

import type { ServerProfile, VerifiedServer } from "./types";

type ServerInfo = {
  serverVersion: string;
  apiMajorVersion: number;
  initialized: boolean;
};

type AuthSession = {
  authenticated: boolean;
  accessMode: "anonymous" | "localTrusted" | "cookie" | "bearer";
  apiMajorVersion: number;
};

const SERVER_VERIFICATION_TIMEOUT_MS = 5_000;
const SERVER_CONNECTION_ERROR =
  "Could not connect to the Agw Server. Check the Server URL and network, then try again.";
const SERVER_VERIFICATION_TIMEOUT_ERROR =
  "Server verification timed out after 5 seconds. Check the Server URL, network, firewall, and Server availability, then try again.";
const SERVER_RESPONSE_ERROR = "The Agw Server returned an invalid response.";

function isServerInfo(value: unknown): value is ServerInfo {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const info = value as Partial<ServerInfo>;
  return (
    typeof info.serverVersion === "string" &&
    typeof info.apiMajorVersion === "number" &&
    typeof info.initialized === "boolean"
  );
}

function isAuthSession(value: unknown): value is AuthSession {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const session = value as Partial<AuthSession>;
  return (
    typeof session.authenticated === "boolean" &&
    (session.accessMode === "anonymous" ||
      session.accessMode === "localTrusted" ||
      session.accessMode === "cookie" ||
      session.accessMode === "bearer") &&
    typeof session.apiMajorVersion === "number"
  );
}

export async function verifyServerProfile(
  profile: ServerProfile,
  token: string,
  onUnauthorized?: () => void,
): Promise<VerifiedServer> {
  if (profile.serverUrl.startsWith("http://") && !profile.allowInsecureHttp) {
    throw new Error("Confirm the HTTP security warning before connecting to this server.");
  }

  const client = createBearerApiClient({ baseUrl: profile.serverUrl, token, onUnauthorized });
  const controller = new AbortController();
  let timedOut = false;
  const timeoutId = setTimeout(() => {
    timedOut = true;
    controller.abort();
  }, SERVER_VERIFICATION_TIMEOUT_MS);

  const request = async <T>(perform: () => Promise<T>): Promise<T> => {
    try {
      return await perform();
    } catch (caught) {
      if (caught instanceof ApiError) throw caught;
      if (!(caught instanceof ApiTransportError)) throw caught;
      throw new Error(timedOut ? SERVER_VERIFICATION_TIMEOUT_ERROR : SERVER_CONNECTION_ERROR);
    }
  };

  try {
    const info = await request(
      () => client.apiGet("/api/server-info", { signal: controller.signal }) as Promise<ServerInfo>,
    );
    if (!isServerInfo(info)) {
      throw new Error(SERVER_RESPONSE_ERROR);
    }
    if (!info.initialized) {
      throw new Error("This Agw Server has not completed setup.");
    }
    if (info.apiMajorVersion !== profile.apiMajorVersion) {
      throw new Error(
        `Server API major ${info.apiMajorVersion} is incompatible with Mobile API major ${profile.apiMajorVersion}.`,
      );
    }

    const session = await request(
      () =>
        client.apiGet(
          "/api/auth/session" as never,
          { signal: controller.signal } as never,
        ) as Promise<AuthSession>,
    );
    if (!isAuthSession(session)) {
      throw new Error(SERVER_RESPONSE_ERROR);
    }
    if (!session.authenticated || session.accessMode !== "bearer") {
      throw new Error("The API token is invalid or has been revoked.");
    }
    if (session.apiMajorVersion !== profile.apiMajorVersion) {
      throw new Error("The authenticated API version is incompatible with this Mobile app.");
    }

    return { profile, token, client };
  } finally {
    clearTimeout(timeoutId);
  }
}
