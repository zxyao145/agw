"use client";

import * as React from "react";
import { RefreshCw } from "lucide-react";

import { Button, cn } from "@agw/components";
import {
  executionReconnectDelaysMs,
  type ExecutionReconnectState,
} from "../../../services/execution-hub";

/** 计算距离下一次自动重连的剩余毫秒数。 */
function useReconnectCountdown(state: ExecutionReconnectState): number {
  const [remainingMs, setRemainingMs] = React.useState(state.retryDelayMs);

  React.useEffect(() => {
    const deadline = Date.now() + state.retryDelayMs;
    const updateRemaining = () => setRemainingMs(Math.max(0, deadline - Date.now()));
    updateRemaining();
    if (state.retryDelayMs === 0) return;

    const intervalId = window.setInterval(updateRemaining, 250);
    return () => window.clearInterval(intervalId);
  }, [state.retryAttempt, state.retryDelayMs]);

  return remainingMs;
}

type ExecutionReconnectingDialogProps = {
  /** 当前自动重连进度或重试耗尽状态。 */
  state: ExecutionReconnectState;
  /** 自动重试耗尽后，由用户触发一次立即重连。 */
  onRetry: () => void;
};

/** 在 SignalR 自动重连期间阻塞 Chat 工作区，并保留 Desktop 顶栏逃生入口。 */
export function ExecutionReconnectingDialog({ state, onRetry }: ExecutionReconnectingDialogProps) {
  const isFailed = state.status === "failed";
  const remainingSeconds = Math.ceil(useReconnectCountdown(state) / 1_000);
  const retryMessage = isFailed
    ? "Failed to rejoin. Please retry or reload the page."
    : remainingSeconds > 0
      ? `Trying again in ${remainingSeconds} second${remainingSeconds === 1 ? "" : "s"}…`
      : "Trying again now…";

  return (
    <div
      className="absolute inset-0 z-40 flex items-start justify-center bg-background/72 px-5 pb-5 pt-[150px] backdrop-blur-[2px]"
      role="presentation"
    >
      <section
        role="dialog"
        aria-labelledby="execution-reconnecting-title"
        aria-describedby="execution-reconnecting-description"
        className="w-full max-w-md rounded-2xl border border-border/80 bg-card/96 p-6 text-card-foreground shadow-2xl shadow-black/15 ring-1 ring-primary/15"
      >
        <p className="sr-only" role="status">
          {isFailed
            ? "Execution connection failed. Manual retry is available."
            : "Execution connection interrupted. Reconnecting to Server."}
        </p>
        <div className="flex items-start gap-4">
          <div className="relative grid size-11 shrink-0 place-items-center rounded-xl border bg-background shadow-sm">
            <RefreshCw
              className={cn(
                "size-5 text-primary",
                !isFailed && "animate-spin [animation-duration:1.5s]",
              )}
              aria-hidden="true"
            />
            <span
              className={cn(
                "absolute -right-1 -top-1 size-2.5 rounded-full ring-4 ring-card",
                isFailed ? "bg-destructive" : "animate-pulse bg-primary",
              )}
            />
          </div>

          <div className="min-w-0 flex-1">
            <p className="text-[0.68rem] font-semibold uppercase tracking-[0.18em] text-primary">
              Connection interrupted
            </p>
            <h2 id="execution-reconnecting-title" className="mt-1 text-base font-semibold">
              Reconnecting to Server…
            </h2>
            <p
              id="execution-reconnecting-description"
              className="mt-1.5 text-sm leading-6 text-muted-foreground"
            >
              Chat is temporarily paused while the live connection is restored.
            </p>
          </div>
        </div>

        <div className="mt-5 rounded-xl border border-border/70 bg-muted/35 px-4 py-3">
          <div className="flex items-center gap-2 text-sm">
            <span
              className={cn(
                "size-2 rounded-full",
                isFailed ? "bg-destructive" : "animate-pulse bg-amber-500",
              )}
              aria-hidden="true"
            />
            <span className={cn("flex-1 font-medium", isFailed && "text-destructive")}>
              {retryMessage}
            </span>
            <span className="text-xs tabular-nums text-muted-foreground">
              {state.retryAttempt}/{executionReconnectDelaysMs.length}
            </span>
          </div>
          {isFailed ? (
            <Button type="button" size="sm" className="mt-3 w-full" onClick={onRetry}>
              <RefreshCw className="size-4" aria-hidden="true" />
              Retry
            </Button>
          ) : (
            <div className="mt-3 flex gap-1" aria-hidden="true">
              {executionReconnectDelaysMs.map((_, index) => (
                <span
                  key={index}
                  className={cn(
                    "h-1 flex-1 rounded-full bg-border transition-colors",
                    index < state.retryAttempt && "bg-primary/70",
                    index + 1 === state.retryAttempt && "animate-pulse bg-primary",
                  )}
                />
              ))}
            </div>
          )}
        </div>

        <p className="mt-4 text-center text-xs leading-5 text-muted-foreground">
          You can still switch Server or open Settings while Agw reconnects.
        </p>
      </section>
    </div>
  );
}
