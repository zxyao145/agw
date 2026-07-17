"use client";

import * as React from "react";

import { useDesktopRuntime } from "@/features/desktop/runtime-provider";
import { executionSessionManager, type ExecutionStatus } from "@/lib/execution-session-manager";

export function useExecutionActivity() {
  React.useSyncExternalStore(
    executionSessionManager.subscribe,
    executionSessionManager.getSnapshot,
    executionSessionManager.getSnapshot,
  );
  const desktop = useDesktopRuntime();
  const serverId = desktop.activeProfile?.id ?? "browser";
  const activeCount = executionSessionManager.getActiveCount();

  React.useEffect(() => {
    if (desktop.isDesktop) void window.agwDesktop?.setActiveTaskCount(activeCount);
  }, [activeCount, desktop.isDesktop]);

  return {
    activeCount,
    getProjectStatus: (projectId: string): ExecutionStatus =>
      executionSessionManager.getProjectStatus(serverId, projectId),
  };
}
