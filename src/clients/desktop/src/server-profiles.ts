export type ServerProfile = {
  id: string;
  kind: "local" | "remote";
  name: string;
  baseUrl: string;
  apiMajorVersion: 1;
  allowInsecureHttp: boolean;
};

export const DEFAULT_LOCAL_PROFILE: ServerProfile = {
  id: "local",
  kind: "local",
  name: "Local",
  baseUrl: "http://127.0.0.1:30815",
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
  const remoteProfiles = profiles.filter((profile) => profile.kind === "remote");
  if (localProfiles.length !== 1 || localProfiles[0]?.id !== "local") {
    throw new Error("Server profiles must contain the local profile.");
  }
  if (remoteProfiles.length > 1) {
    throw new Error("Agw Desktop supports at most one remote profile.");
  }

  for (const profile of profiles) {
    const baseUrl = normalizeServerUrl(profile.baseUrl);
    if (profile.kind === "remote" && baseUrl.startsWith("http://") && !profile.allowInsecureHttp) {
      throw new Error("Remote HTTP requires allowInsecureHttp consent.");
    }
  }
}
