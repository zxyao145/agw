export type ExecutionKeyParts = {
  serverId: string;
  projectId: string;
  contextId: string;
};

export type ExecutionStatus =
  | "idle"
  | "running"
  | "waiting-approval"
  | "completed-unread"
  | "failed-unread"
  | "detached";

const STATUS_PRIORITY: Record<ExecutionStatus, number> = {
  idle: 0,
  "completed-unread": 1,
  detached: 2,
  running: 3,
  "failed-unread": 4,
  "waiting-approval": 5,
};

export function aggregateExecutionStatus(statuses: ExecutionStatus[]): ExecutionStatus {
  return statuses.reduce<ExecutionStatus>(
    (current, status) => (STATUS_PRIORITY[status] > STATUS_PRIORITY[current] ? status : current),
    "idle",
  );
}
