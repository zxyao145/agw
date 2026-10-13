import { posix, win32 } from "node:path";

import envPaths from "env-paths";

export function resolveServerDataDirectory(
  platform: NodeJS.Platform,
  platformDataDirectory: string,
): string {
  const path = platform === "win32" ? win32 : posix;
  const directory =
    platform === "win32" ? path.dirname(platformDataDirectory) : platformDataDirectory;
  const name = platform === "linux" ? "agw" : "Agw";
  if (!path.isAbsolute(directory) || path.basename(directory) !== name) {
    throw new Error("Unexpected system application data directory for Agw Server.");
  }
  return directory;
}

export function getServerDataDirectory(): string {
  const name = process.platform === "linux" ? "agw" : "Agw";
  return resolveServerDataDirectory(process.platform, envPaths(name, { suffix: "" }).data);
}
