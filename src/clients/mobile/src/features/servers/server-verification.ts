import { createBearerApiClient } from "@agw/api";

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

export async function verifyServerProfile(
  profile: ServerProfile,
  token: string,
  onUnauthorized?: () => void,
): Promise<VerifiedServer> {
  if (profile.serverUrl.startsWith("http://") && !profile.allowInsecureHttp) {
    throw new Error("Confirm the HTTP security warning before connecting to this server.");
  }

  const client = createBearerApiClient({ baseUrl: profile.serverUrl, token, onUnauthorized });
  const info = (await client.apiGet("/api/server-info")) as unknown as ServerInfo;
  if (!info.initialized) {
    throw new Error("This Agw Server has not completed setup.");
  }
  if (info.apiMajorVersion !== profile.apiMajorVersion) {
    throw new Error(
      `Server API major ${info.apiMajorVersion} is incompatible with Mobile API major ${profile.apiMajorVersion}.`,
    );
  }

  const session = (await client.apiGet("/api/auth/session" as never)) as unknown as AuthSession;
  if (!session.authenticated || session.accessMode !== "bearer") {
    throw new Error("The API token is invalid or has been revoked.");
  }
  if (session.apiMajorVersion !== profile.apiMajorVersion) {
    throw new Error("The authenticated API version is incompatible with this Mobile app.");
  }

  return { profile, token, client };
}
