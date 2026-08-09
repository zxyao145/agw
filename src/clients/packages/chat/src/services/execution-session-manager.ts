import {
  ExecutionHubClient,
  getPendingHumanGate,
  getTurnFinishedStatus,
  type ExecutionHubHandlers,
  type AgentMode,
  type PermissionMode,
  type ExecutionRequest,
  type ExecutionSetting,
} from "./execution-hub";
import type { AiMessage } from "@agw/api";
import {
  ExecutionActivityStore,
  getExecutionSessionKey,
  type ExecutionSessionKey,
  type ExecutionStatus,
} from "../state/execution-activity-store";

type ExecutionClient = Pick<
  ExecutionHubClient,
  | "configure"
  | "execute"
  | "setMode"
  | "setPermissionMode"
  | "interrupt"
  | "interruptAndWait"
  | "submitHumanResponse"
  | "dispose"
>;

type ClientFactory = (handlers: ExecutionHubHandlers) => ExecutionClient;

type Entry = {
  key: ExecutionSessionKey;
  client: ExecutionClient;
  handler: ExecutionHubHandlers | null;
  pendingMessages: AiMessage[];
};

export type ManagedExecutionHandle = {
  configure(setting: ExecutionSetting): Promise<void>;
  execute(request: ExecutionRequest): Promise<void>;
  setMode(agentId: string, mode: AgentMode): Promise<void>;
  setPermissionMode(permissionMode: PermissionMode): Promise<void>;
  interrupt(reason?: string): Promise<void>;
  interruptAndWait(reason?: string): Promise<void>;
  submitHumanResponse(args: {
    requestId: string;
    approved: boolean;
    responseText?: string | null;
    approvalScope?: "once" | "always-tool" | "always-arguments";
    responseData?: unknown;
  }): Promise<void>;
  getStatus(): ExecutionStatus;
  detach(): void;
  dispose(): Promise<void>;
};

export class ExecutionSessionManager {
  private readonly entries = new Map<string, Entry>();
  private readonly createClient: ClientFactory;
  private readonly activity = new ExecutionActivityStore();

  public constructor(createClient: ClientFactory = (handlers) => new ExecutionHubClient(handlers)) {
    this.createClient = createClient;
  }

  public attach(key: ExecutionSessionKey, handler: ExecutionHubHandlers): ManagedExecutionHandle {
    const id = getExecutionSessionKey(key);
    let entry = this.entries.get(id);
    if (!entry) {
      let nextEntry!: Entry;
      const client = this.createClient({
        onMessage: (message) => this.handleMessage(nextEntry, message),
        onError: (error) => nextEntry.handler?.onError?.(error),
        onClose: (error) => this.handleClose(nextEntry, error),
      });
      nextEntry = {
        key,
        client,
        handler,
        pendingMessages: [],
      };
      entry = nextEntry;
      this.entries.set(id, entry);
    } else {
      entry.handler = handler;
    }
    this.activity.attach(key);
    const pendingMessages = entry.pendingMessages.splice(0);
    if (pendingMessages.length > 0) {
      queueMicrotask(() => {
        for (const message of pendingMessages) handler.onMessage(message);
      });
    }

    const attachedEntry = entry;
    return {
      configure: (setting) => attachedEntry.client.configure(setting),
      execute: async (request) => {
        if (this.activity.isActive(key)) {
          throw new Error("This conversation already has a running task.");
        }
        this.activity.turnStarted(key);
        try {
          await attachedEntry.client.execute(request);
        } catch (error) {
          this.activity.turnFinished(key, "failed");
          throw error;
        }
      },
      setMode: (agentId, mode) => attachedEntry.client.setMode(agentId, mode),
      setPermissionMode: (permissionMode) => attachedEntry.client.setPermissionMode(permissionMode),
      interrupt: (reason) => attachedEntry.client.interrupt(reason),
      interruptAndWait: (reason) => attachedEntry.client.interruptAndWait(reason),
      submitHumanResponse: (args) => attachedEntry.client.submitHumanResponse(args),
      getStatus: () => this.activity.getStatus(key),
      detach: () => {
        if (attachedEntry.handler === handler) {
          attachedEntry.handler = null;
          this.activity.detach(key);
        }
      },
      dispose: async () => {
        if (this.entries.get(id) !== attachedEntry) return;
        this.entries.delete(id);
        this.activity.remove(key);
        await attachedEntry.client.dispose();
      },
    };
  }

  public has(key: ExecutionSessionKey): boolean {
    return this.entries.has(getExecutionSessionKey(key));
  }

  public getProjectStatus(serverId: string, projectId: string): ExecutionStatus {
    return this.activity.getProjectStatus(serverId, projectId);
  }

  public getActiveCount(): number {
    return this.activity.getActiveCount();
  }

  public subscribe = this.activity.subscribe;

  public getSnapshot = this.activity.getSnapshot;

  private handleMessage(entry: Entry, message: AiMessage): void {
    if (message.additionalProperties?.type === "turn-start") {
      this.activity.turnStarted(entry.key);
    } else if (getPendingHumanGate(message)) {
      this.activity.waitingForApproval(entry.key);
    } else {
      const terminalStatus = getTurnFinishedStatus(message);
      if (terminalStatus) {
        this.activity.turnFinished(entry.key, terminalStatus);
      }
    }
    if (entry.handler) {
      entry.handler.onMessage(message);
    } else {
      entry.pendingMessages.push(message);
      if (entry.pendingMessages.length > 200) entry.pendingMessages.shift();
    }
  }

  private handleClose(entry: Entry, error?: Error): void {
    this.activity.connectionClosed(entry.key, error);
    entry.handler?.onClose?.(error);
  }
}

export const executionSessionManager = new ExecutionSessionManager();
