import { createParser } from "eventsource-parser";
import { isNotificationRevisionNewer } from "@agw/notifications/notification-state";
import type { DesktopNotification } from "../shared/contracts";

const MAX_REMEMBERED_NOTIFICATIONS = 2048;

export type DesktopNotificationProfile = {
  serverId: string;
  baseUrl: string;
  token: string;
};

export type DesktopNotificationFrame = {
  serverId: string;
  notification: DesktopNotification;
};

export type NotificationStreamEvent =
  | { type: "ready"; connectedAt: string }
  | { type: "notification"; id: string; notification: DesktopNotification };

export type NotificationStreamUpdate = {
  notification: DesktopNotification;
  showToast: boolean;
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function requiredString(value: unknown, name: string): string {
  if (typeof value !== "string")
    throw new Error(`Notification stream field ${name} must be a string.`);
  return value;
}

function nullableString(value: unknown, name: string): string | null {
  if (value === null) return null;
  return requiredString(value, name);
}

function parseNotification(value: unknown): DesktopNotification {
  if (!isRecord(value)) throw new Error("Notification stream data must be an object.");
  const kind = value.kind;
  const state = value.state;
  const version = value.version;
  if (typeof kind !== "number" && typeof kind !== "string") {
    throw new Error("Notification stream field kind must be a string or number.");
  }
  if (typeof state !== "number" && typeof state !== "string") {
    throw new Error("Notification stream field state must be a string or number.");
  }
  if (typeof version !== "number" || !Number.isSafeInteger(version) || version < 1) {
    throw new Error("Notification stream field version must be a positive integer.");
  }
  const createdAt = requiredString(value.createdAt, "createdAt");
  const changedAt = requiredString(value.changedAt, "changedAt");
  if (!Number.isFinite(Date.parse(createdAt)) || !Number.isFinite(Date.parse(changedAt))) {
    throw new Error("Notification stream timestamps must be valid date-time strings.");
  }
  return {
    id: requiredString(value.id, "id"),
    createdAt,
    kind,
    state,
    title: requiredString(value.title, "title"),
    text: requiredString(value.text, "text"),
    link: nullableString(value.link, "link"),
    readAt: nullableString(value.readAt, "readAt"),
    changedAt,
    version,
  };
}

export function createNotificationStreamParser(onEvent: (event: NotificationStreamEvent) => void) {
  return createParser({
    onEvent(message) {
      if (message.event === "ready") {
        const value: unknown = JSON.parse(message.data);
        if (!isRecord(value)) throw new Error("Notification stream ready data must be an object.");
        const connectedAt = requiredString(value.connectedAt, "connectedAt");
        if (!Number.isFinite(Date.parse(connectedAt))) {
          throw new Error("Notification stream connectedAt must be a valid date-time string.");
        }
        onEvent({ type: "ready", connectedAt });
        return;
      }
      if (message.event !== "notification") return;
      if (!message.id) throw new Error("Notification stream event ID is required.");
      onEvent({
        type: "notification",
        id: message.id,
        notification: parseNotification(JSON.parse(message.data)),
      });
    },
  });
}

export class NotificationStreamReducer {
  private connectedAt: number | null = null;
  private readonly revisions = new Map<
    string,
    { version: number; changedAt: string; readAt: string | null }
  >();
  private readonly toastedIds = new Set<string>();

  public beginConnection(): void {
    this.connectedAt = null;
  }

  public resetProfile(): void {
    this.connectedAt = null;
    this.revisions.clear();
    this.toastedIds.clear();
  }

  public apply(event: NotificationStreamEvent): NotificationStreamUpdate | null {
    if (event.type === "ready") {
      this.connectedAt = Date.parse(event.connectedAt);
      return null;
    }
    const { notification } = event;
    const knownRevision = this.revisions.get(notification.id);
    if (knownRevision && !isNotificationRevisionNewer(notification, knownRevision)) return null;
    this.revisions.set(notification.id, {
      version: notification.version,
      changedAt: notification.changedAt,
      readAt: notification.readAt,
    });
    if (this.revisions.size > MAX_REMEMBERED_NOTIFICATIONS) {
      const oldestId = this.revisions.keys().next().value;
      if (oldestId !== undefined) this.revisions.delete(oldestId);
    }
    const showToast =
      this.connectedAt !== null &&
      Date.parse(notification.createdAt) > this.connectedAt &&
      !this.toastedIds.has(notification.id);
    if (showToast) {
      this.toastedIds.add(notification.id);
      if (this.toastedIds.size > MAX_REMEMBERED_NOTIFICATIONS) {
        const oldestId = this.toastedIds.values().next().value;
        if (oldestId !== undefined) this.toastedIds.delete(oldestId);
      }
    }
    return {
      notification,
      showToast,
    };
  }
}

export type DesktopNotificationStreamOptions = {
  fetch: typeof fetch;
  onNotification(
    profile: DesktopNotificationProfile,
    notification: DesktopNotification,
    showToast: boolean,
  ): void | Promise<void>;
  onError(error: unknown): void;
  retryDelayMs?: number;
};

export class DesktopNotificationStream {
  private profile: DesktopNotificationProfile | null = null;
  private profileKey: string | null = null;
  private controller: AbortController | null = null;
  private lastEventId: string | null = null;
  private readonly reducer = new NotificationStreamReducer();

  public constructor(private readonly options: DesktopNotificationStreamOptions) {}

  public setProfile(profile: DesktopNotificationProfile | null): void {
    const profileKey = profile
      ? JSON.stringify([profile.serverId, profile.baseUrl.replace(/\/+$/u, ""), profile.token])
      : null;
    if (profileKey === this.profileKey) return;
    this.controller?.abort();
    this.controller = null;
    this.profile = profile ? { ...profile, baseUrl: profile.baseUrl.replace(/\/+$/u, "") } : null;
    this.profileKey = profileKey;
    this.lastEventId = null;
    this.reducer.resetProfile();
    if (this.profile) {
      const controller = new AbortController();
      this.controller = controller;
      void this.run(this.profile, controller.signal);
    }
  }

  public stop(): void {
    this.controller?.abort();
    this.controller = null;
    this.profile = null;
    this.profileKey = null;
    this.lastEventId = null;
    this.reducer.resetProfile();
  }

  private async run(profile: DesktopNotificationProfile, signal: AbortSignal): Promise<void> {
    while (!signal.aborted) {
      this.reducer.beginConnection();
      try {
        const headers: Record<string, string> = {
          Accept: "text/event-stream",
          Authorization: `Bearer ${profile.token}`,
        };
        if (this.lastEventId) headers["Last-Event-ID"] = this.lastEventId;
        const response = await this.options.fetch(`${profile.baseUrl}/api/notifications/stream`, {
          headers,
          signal,
          cache: "no-store",
          credentials: "omit",
        });
        if (!response.ok) throw new Error(`Notification stream returned HTTP ${response.status}.`);
        if (!response.body) throw new Error("Notification stream response has no body.");

        const parser = createNotificationStreamParser((event) => {
          if (event.type === "ready") {
            this.reducer.apply(event);
            return;
          }
          this.lastEventId = event.id;
          const update = this.reducer.apply(event);
          if (update) {
            void Promise.resolve(
              this.options.onNotification(profile, update.notification, update.showToast),
            ).catch(this.options.onError);
          }
        });
        const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
        try {
          while (!signal.aborted) {
            const { done, value } = await reader.read();
            if (done) break;
            parser.feed(value);
          }
        } finally {
          reader.releaseLock();
        }
        if (!signal.aborted) throw new Error("Notification stream closed.");
      } catch (error) {
        if (signal.aborted) return;
        this.options.onError(error);
        await wait(this.options.retryDelayMs ?? 5000, signal);
      }
    }
  }
}

function wait(delayMs: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve();
      return;
    }
    const timeout = setTimeout(done, delayMs);
    function done() {
      clearTimeout(timeout);
      signal.removeEventListener("abort", done);
      resolve();
    }
    signal.addEventListener("abort", done, { once: true });
  });
}
