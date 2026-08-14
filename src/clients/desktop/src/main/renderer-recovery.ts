import { appendFile, mkdir } from "node:fs/promises";
import { dirname } from "node:path";

export const RENDERER_STABLE_RESET_MS = 60_000;

export type RendererRecoveryAction = "auto-reload" | "manual-recovery";

type Schedule = (callback: () => void, delayMs: number) => unknown;
type Cancel = (timer: unknown) => void;

/** Limits automatic recovery to one attempt until the renderer remains stable for 60 seconds. */
export class RendererRecoveryGuard {
  private automaticRecoveryUsed = false;
  private stableTimer: unknown;

  public constructor(
    private readonly stableResetMs = RENDERER_STABLE_RESET_MS,
    private readonly schedule: Schedule = (callback, delayMs) => setTimeout(callback, delayMs),
    private readonly cancel: Cancel = (timer) =>
      clearTimeout(timer as ReturnType<typeof setTimeout>),
  ) {}

  public recordFailure(): RendererRecoveryAction {
    this.cancelStableTimer();
    if (!this.automaticRecoveryUsed) {
      this.automaticRecoveryUsed = true;
      return "auto-reload";
    }

    return "manual-recovery";
  }

  public markLoadStarted(): void {
    this.cancelStableTimer();
  }

  public markLoadSucceeded(): void {
    if (!this.automaticRecoveryUsed) return;
    this.cancelStableTimer();
    this.stableTimer = this.schedule(() => {
      this.stableTimer = undefined;
      this.automaticRecoveryUsed = false;
    }, this.stableResetMs);
  }

  public dispose(): void {
    this.cancelStableTimer();
  }

  public canAutomaticallyRecover(): boolean {
    return !this.automaticRecoveryUsed;
  }

  private cancelStableTimer(): void {
    if (this.stableTimer !== undefined) {
      this.cancel(this.stableTimer);
      this.stableTimer = undefined;
    }
  }
}

export type RendererEventRecord = {
  timestamp: string;
  event: "render-process-gone" | "did-fail-load" | "unresponsive" | "responsive";
  appVersion: string;
  electronVersion: string;
  os: string;
  reason: string;
  exitCode?: number;
  pathname: string;
};

export function sanitizeRendererPathname(value: string): string {
  try {
    return new URL(value).pathname || "/";
  } catch {
    return "/";
  }
}

export function sanitizeRendererReason(value: string): string {
  const errorName = value.match(/\bERR_[A-Z0-9_]+\b/u)?.[0];
  if (errorName) return errorName;
  return /^[a-z][a-z0-9-]{0,63}$/u.test(value) ? value : "unknown";
}

export function createRendererEventRecord(
  input: Pick<RendererEventRecord, "event" | "reason" | "pathname"> &
    Partial<Pick<RendererEventRecord, "exitCode">>,
  environment: Pick<RendererEventRecord, "appVersion" | "electronVersion" | "os"> & {
    now?: Date;
  },
): RendererEventRecord {
  return {
    timestamp: (environment.now ?? new Date()).toISOString(),
    event: input.event,
    appVersion: environment.appVersion,
    electronVersion: environment.electronVersion,
    os: environment.os,
    reason: sanitizeRendererReason(input.reason),
    ...(input.exitCode === undefined ? {} : { exitCode: input.exitCode }),
    pathname: sanitizeRendererPathname(input.pathname),
  };
}

export async function appendRendererEvent(
  filePath: string,
  event: RendererEventRecord,
): Promise<void> {
  await mkdir(dirname(filePath), { recursive: true });
  await appendFile(filePath, `${JSON.stringify(event)}\n`, { encoding: "utf8", mode: 0o600 });
}
