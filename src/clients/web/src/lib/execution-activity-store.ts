export type ExecutionSessionKey = {
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

type Activity = {
  key: ExecutionSessionKey;
  attached: boolean;
  activeTurn: boolean;
  status: ExecutionStatus;
};

const STATUS_PRIORITY: Record<ExecutionStatus, number> = {
  idle: 0,
  "completed-unread": 1,
  detached: 2,
  running: 3,
  "failed-unread": 4,
  "waiting-approval": 5,
};

export function getExecutionSessionKey(key: ExecutionSessionKey): string {
  return JSON.stringify([key.serverId, key.projectId, key.contextId]);
}

export function aggregateExecutionStatus(statuses: ExecutionStatus[]): ExecutionStatus {
  return statuses.reduce<ExecutionStatus>(
    (current, status) => (STATUS_PRIORITY[status] > STATUS_PRIORITY[current] ? status : current),
    "idle",
  );
}

export class ExecutionActivityStore {
  private readonly activities = new Map<string, Activity>();
  private readonly listeners = new Set<() => void>();
  private version = 0;

  public attach(key: ExecutionSessionKey): void {
    const activity = this.getOrCreate(key);
    activity.attached = true;
    if (activity.status === "completed-unread" || activity.status === "failed-unread") {
      this.setStatus(activity, "idle");
    }
  }

  public detach(key: ExecutionSessionKey): void {
    this.getOrCreate(key).attached = false;
  }

  public turnStarted(key: ExecutionSessionKey): void {
    const activity = this.getOrCreate(key);
    activity.activeTurn = true;
    this.setStatus(activity, "running");
  }

  public waitingForApproval(key: ExecutionSessionKey): void {
    this.setStatus(this.getOrCreate(key), "waiting-approval");
  }

  public turnFinished(
    key: ExecutionSessionKey,
    result: "completed" | "interrupted" | "failed",
  ): void {
    const activity = this.getOrCreate(key);
    activity.activeTurn = false;
    this.setStatus(
      activity,
      activity.attached ? "idle" : result === "failed" ? "failed-unread" : "completed-unread",
    );
  }

  public connectionClosed(key: ExecutionSessionKey, error?: Error): void {
    const activity = this.getOrCreate(key);
    const wasActive = activity.activeTurn;
    activity.activeTurn = false;
    this.setStatus(activity, wasActive ? (error ? "failed-unread" : "detached") : "idle");
  }

  public remove(key: ExecutionSessionKey): void {
    if (this.activities.delete(getExecutionSessionKey(key))) this.emitChange();
  }

  public getStatus(key: ExecutionSessionKey): ExecutionStatus {
    return this.activities.get(getExecutionSessionKey(key))?.status ?? "idle";
  }

  public isActive(key: ExecutionSessionKey): boolean {
    return this.activities.get(getExecutionSessionKey(key))?.activeTurn ?? false;
  }

  public getProjectStatus(serverId: string, projectId: string): ExecutionStatus {
    return aggregateExecutionStatus(
      [...this.activities.values()]
        .filter(
          (activity) => activity.key.serverId === serverId && activity.key.projectId === projectId,
        )
        .map((activity) => activity.status),
    );
  }

  public getActiveCount(): number {
    return [...this.activities.values()].filter((activity) =>
      ["running", "waiting-approval", "detached"].includes(activity.status),
    ).length;
  }

  public subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public getSnapshot = (): number => this.version;

  private getOrCreate(key: ExecutionSessionKey): Activity {
    const id = getExecutionSessionKey(key);
    let activity = this.activities.get(id);
    if (!activity) {
      activity = { key, attached: false, activeTurn: false, status: "idle" };
      this.activities.set(id, activity);
    }
    return activity;
  }

  private setStatus(activity: Activity, status: ExecutionStatus): void {
    if (activity.status === status) return;
    activity.status = status;
    this.emitChange();
  }

  private emitChange(): void {
    this.version += 1;
    for (const listener of this.listeners) listener();
  }
}
