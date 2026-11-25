import { cn } from "@agw/components";
import type { ExecutionStatus } from "@agw/chat-runtime";

export const STATUS_LABEL: Record<ExecutionStatus, string> = {
  idle: "Idle",
  running: "Running",
  "waiting-approval": "Waiting for approval",
  "completed-unread": "Completed",
  "failed-unread": "Failed",
  detached: "Detached",
};

export function StatusDot({ status }: { status: ExecutionStatus }) {
  return (
    <span className={cn("agw-status-dot", `is-${status}`)} aria-label={STATUS_LABEL[status]} />
  );
}
