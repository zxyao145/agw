import type { NotificationKind, NotificationListItem, NotificationState } from "@agw/api";

export type NotificationStreamItem = NotificationListItem & { createdAt: string };

export function isNotificationRevisionNewer(
  incoming: Pick<NotificationListItem, "version" | "changedAt" | "readAt">,
  existing: Pick<NotificationListItem, "version" | "changedAt" | "readAt">,
): boolean {
  if (incoming.version !== existing.version) return incoming.version > existing.version;
  const changedAt = Date.parse(incoming.changedAt) - Date.parse(existing.changedAt);
  if (changedAt !== 0) return changedAt > 0;
  return existing.readAt === null && incoming.readAt !== null;
}

export function mergeNotificationItem(
  items: readonly NotificationListItem[],
  incoming: NotificationListItem,
): NotificationListItem[] {
  const existing = items.find((item) => item.id === incoming.id);
  if (existing && !isNotificationRevisionNewer(incoming, existing)) return [...items];
  return [...items.filter((item) => item.id !== incoming.id), incoming].sort((left, right) => {
    const changedAt = Date.parse(right.changedAt) - Date.parse(left.changedAt);
    return changedAt || right.id.localeCompare(left.id);
  });
}

function parseKind(value: unknown): NotificationKind {
  if (value === 0 || value === "Notice") return 0;
  if (value === 1 || value === "Question") return 1;
  throw new Error("Notification stream field kind has an unsupported value.");
}

function parseState(value: unknown): NotificationState {
  if (value === 0 || value === "Open") return 0;
  if (value === 1 || value === "Answered") return 1;
  if (value === 2 || value === "Resolved") return 2;
  if (value === 3 || value === "Closed") return 3;
  throw new Error("Notification stream field state has an unsupported value.");
}

function requiredString(value: unknown, field: string): string {
  if (typeof value !== "string")
    throw new Error(`Notification stream field ${field} must be a string.`);
  return value;
}

function nullableString(value: unknown, field: string): string | null {
  if (value === null) return null;
  return requiredString(value, field);
}

export function parseNotificationStreamItem(value: unknown): NotificationStreamItem {
  if (typeof value !== "object" || value === null)
    throw new Error("Notification stream item must be an object.");
  const item = value as Record<string, unknown>;
  const id = requiredString(item.id, "id");
  const createdAt = requiredString(item.createdAt, "createdAt");
  const title = requiredString(item.title, "title");
  const text = requiredString(item.text, "text");
  const changedAt = requiredString(item.changedAt, "changedAt");
  const version = item.version;
  if (typeof version !== "number" || !Number.isSafeInteger(version) || version < 1) {
    throw new Error("Notification stream field version must be a positive integer.");
  }
  if (!Number.isFinite(Date.parse(createdAt)) || !Number.isFinite(Date.parse(changedAt))) {
    throw new Error("Notification stream timestamps must be valid date-time strings.");
  }
  const kind = parseKind(item.kind);
  const state = parseState(item.state);
  return {
    id,
    createdAt,
    kind,
    state,
    title,
    text,
    link: nullableString(item.link, "link"),
    readAt: nullableString(item.readAt, "readAt"),
    changedAt,
    version,
  };
}
