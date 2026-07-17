import { posix, resolve, win32 } from "node:path";

export function resolveServerExecutablePath(
  resourcesPath: string,
  platform: NodeJS.Platform,
  overridePath?: string,
): string {
  if (overridePath) return resolve(overridePath);
  const executable = platform === "win32" ? "agw-server.exe" : "agw-server";
  const path = platform === "win32" ? win32 : posix;
  return path.join(resourcesPath, "server", executable);
}
