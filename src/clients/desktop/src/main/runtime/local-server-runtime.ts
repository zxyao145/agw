import { readFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";

import type { LocalServerRuntime } from "../../shared/contracts";

export function parseLocalServerRuntime(value: string): LocalServerRuntime | null {
  try {
    const runtime = JSON.parse(value) as Partial<LocalServerRuntime>;
    const url = new URL(runtime.baseUrl ?? "");
    const loopback =
      url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
    if (
      runtime.schemaVersion !== 1 ||
      runtime.apiMajorVersion !== 1 ||
      !Number.isSafeInteger(runtime.pid) ||
      !Number.isSafeInteger(runtime.port) ||
      runtime.port !== Number(url.port) ||
      url.protocol !== "http:" ||
      !loopback ||
      typeof runtime.serverVersion !== "string" ||
      typeof runtime.startedAt !== "string"
    ) {
      return null;
    }
    return runtime as LocalServerRuntime;
  } catch {
    return null;
  }
}

function isProcessAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return (error as NodeJS.ErrnoException).code === "EPERM";
  }
}

export async function readLocalServerRuntime(): Promise<LocalServerRuntime | null> {
  try {
    const runtime = parseLocalServerRuntime(
      await readFile(join(homedir(), "agw", "runtime", "server.json"), "utf8"),
    );
    return runtime && isProcessAlive(runtime.pid) ? runtime : null;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT") return null;
    throw error;
  }
}
