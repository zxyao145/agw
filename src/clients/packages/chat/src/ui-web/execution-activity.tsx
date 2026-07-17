"use client";

import * as React from "react";

import { executionSessionManager } from "../services/execution-session-manager";
import type { ExecutionStatus } from "../state/execution-activity-store";
import { useExecutionPlatform } from "./execution-platform";

export function useExecutionActivity() {
  React.useSyncExternalStore(
    executionSessionManager.subscribe,
    executionSessionManager.getSnapshot,
    executionSessionManager.getSnapshot,
  );
  const platform = useExecutionPlatform();
  const serverId = platform.serverId;
  const activeCount = executionSessionManager.getActiveCount();

  React.useEffect(() => {
    platform.onActiveCountChange?.(activeCount);
  }, [activeCount, platform]);

  return {
    activeCount,
    getProjectStatus: (projectId: string): ExecutionStatus =>
      executionSessionManager.getProjectStatus(serverId, projectId),
  };
}
