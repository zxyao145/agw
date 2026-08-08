import type { ServerProfile } from "../../shared/contracts";

export type { ServerProfile } from "../../shared/contracts";

export const DEFAULT_LOCAL_PROFILE: ServerProfile = {
  id: "local",
  kind: "local",
  name: "Local",
  baseUrl: "http://127.0.0.1:30816",
  apiMajorVersion: 1,
  allowInsecureHttp: true,
};

export function normalizeServerUrl(value: string): string {
  const url = new URL(value.trim());
  if (url.protocol !== "http:" && url.protocol !== "https:") {
    throw new Error("Server URL must use HTTP or HTTPS.");
  }
  url.pathname = url.pathname.replace(/\/+$/u, "");
  url.search = "";
  url.hash = "";
  return url.toString().replace(/\/$/u, "");
}

export function validateServerProfiles(profiles: ServerProfile[]): void {
  const localProfiles = profiles.filter((profile) => profile.kind === "local");
  if (localProfiles.length !== 1 || localProfiles[0]?.id !== "local") {
    throw new Error("Server profiles must contain the local profile.");
  }

  const profileIds = new Set<string>();
  for (const profile of profiles) {
    if (!profile.id.trim() || profileIds.has(profile.id)) {
      throw new Error("Server profile IDs must be non-empty and unique.");
    }
    profileIds.add(profile.id);

    const baseUrl = normalizeServerUrl(profile.baseUrl);
    if (profile.kind === "remote" && baseUrl.startsWith("http://") && !profile.allowInsecureHttp) {
      throw new Error("Remote HTTP requires allowInsecureHttp consent.");
    }
  }
}
