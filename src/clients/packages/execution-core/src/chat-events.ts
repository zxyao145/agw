import type {
  ChatChannel,
  ChatEvent,
  ChatEventFilter,
  ChatEventKind,
  ChatEventOrigin,
  ChatMessageSource,
  ExecutionMessage,
} from "@agw/execution-types";

const CHAT_EVENT_KINDS: readonly ChatEventKind[] = ["request", "response", "message"];
const CHAT_CHANNELS: readonly ChatChannel[] = ["http", "execution"];
const CHAT_EVENT_ORIGIN_KINDS: readonly ChatEventOrigin["kind"][] = ["client", "extension"];
const CHAT_MESSAGE_SOURCES: readonly ChatMessageSource[] = ["live", "history"];
const SCOPE_FIELDS = ["serverId", "projectId", "conversationId"] as const;
const FILTER_FIELDS: ReadonlySet<string> = new Set([
  "kinds",
  "channels",
  "origins",
  ...SCOPE_FIELDS,
  "sources",
  "roles",
  "messageTypes",
  "contentTypes",
]);

/** 条件合法时返回 null，否则返回问题描述。Returns null for a valid filter, otherwise a description of the problem. */
export function validateChatEventFilter(value: unknown): string | null {
  if (!isPlainObject(value)) {
    return "The chat event filter must be a plain object.";
  }
  for (const key of Object.keys(value)) {
    if (!FILTER_FIELDS.has(key)) {
      return `The chat event filter has an unknown field "${key}".`;
    }
  }
  for (const field of SCOPE_FIELDS) {
    const scopeValue = value[field];
    if (scopeValue !== undefined && !isNonEmptyString(scopeValue)) {
      return `The chat event filter field "${field}" must be a non-empty string.`;
    }
  }
  return (
    validateArray(value, "kinds", (item) => CHAT_EVENT_KINDS.includes(item as ChatEventKind)) ??
    validateArray(value, "channels", (item) => CHAT_CHANNELS.includes(item as ChatChannel)) ??
    validateArray(value, "origins", (item) =>
      CHAT_EVENT_ORIGIN_KINDS.includes(item as ChatEventOrigin["kind"]),
    ) ??
    validateArray(value, "sources", (item) =>
      CHAT_MESSAGE_SOURCES.includes(item as ChatMessageSource),
    ) ??
    validateArray(value, "roles", isNonEmptyString) ??
    validateArray(value, "messageTypes", (item) => item === null || isNonEmptyString(item)) ??
    validateArray(value, "contentTypes", isNonEmptyString)
  );
}

/**
 * 省略的字段不限制；给出的字段全部满足才匹配，数组字段中任一值满足即可。给出消息字段时，request 与 response 事件不匹配。
 * Omitted fields do not restrict; every given field must hold, and any value of an array field satisfies it. Requests
 * and responses never match a filter that gives a message field.
 */
export function matchesChatEvent(filter: ChatEventFilter, event: ChatEvent): boolean {
  if (filter.kinds && !filter.kinds.includes(event.kind)) return false;
  if (filter.channels && !filter.channels.includes(event.channel)) return false;
  if (filter.origins && !filter.origins.includes(event.origin.kind)) return false;
  for (const field of SCOPE_FIELDS) {
    const expected = filter[field];
    if (expected !== undefined && event.scope[field] !== expected) return false;
  }
  if (!filter.sources && !filter.roles && !filter.messageTypes && !filter.contentTypes) {
    return true;
  }
  if (event.kind !== "message") return false;
  if (filter.sources && !filter.sources.includes(event.source)) return false;
  const { message } = event;
  if (filter.roles) {
    // 服务端 AiRole 的相等比较不区分大小写。
    // The server's AiRole equality ignores case.
    const role = message.role?.toLowerCase();
    if (role === undefined || !filter.roles.some((item) => item.toLowerCase() === role)) {
      return false;
    }
  }
  if (filter.messageTypes) {
    const type = message.additionalProperties?.type;
    if (!filter.messageTypes.includes(typeof type === "string" ? type : null)) return false;
  }
  if (
    filter.contentTypes &&
    !message.contents.some((content) => filter.contentTypes!.includes(content.type))
  ) {
    return false;
  }
  return true;
}

/**
 * 事件的 JSON 副本，递归冻结；值为 undefined 的字段不在副本中。
 * A recursively frozen JSON copy of the event; fields whose value is undefined are absent from it.
 */
export function snapshotChatEvent<TMessage extends ExecutionMessage>(
  event: ChatEvent<TMessage>,
): ChatEvent<TMessage> {
  return deepFreeze(JSON.parse(JSON.stringify(event)) as ChatEvent<TMessage>);
}

function deepFreeze<T>(value: T): T {
  if (value !== null && typeof value === "object") {
    for (const item of Object.values(value)) {
      deepFreeze(item);
    }
    Object.freeze(value);
  }
  return value;
}

function validateArray(
  filter: Record<string, unknown>,
  field: string,
  isAllowed: (item: unknown) => boolean,
): string | null {
  const value = filter[field];
  if (value === undefined) return null;
  if (!Array.isArray(value) || value.length === 0) {
    return `The chat event filter field "${field}" must be a non-empty array.`;
  }
  const invalid = value.findIndex((item) => !isAllowed(item));
  return invalid < 0
    ? null
    : `The chat event filter field "${field}" has an invalid value at index ${invalid}.`;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (value === null || typeof value !== "object") return false;
  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.length > 0;
}
