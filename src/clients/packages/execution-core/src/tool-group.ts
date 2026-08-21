import type { ExecutionMessage, ExecutionMessageContent } from "./types";

export const FunctionCallContent = "FunctionCallContent";
export const FunctionResultContent = "FunctionResultContent";

export type ProcessedMessageItem<T extends ExecutionMessage = ExecutionMessage> =
  | { type: "accordion"; messages: T[]; toolName: string }
  | { type: "normal"; message: T }
  | { type: "result"; message: T };

export type MessageFragmentType = "normal" | "result" | "function-call" | "function-result";

export type MessageFragment<T extends ExecutionMessage = ExecutionMessage> = {
  type: MessageFragmentType;
  message: T;
  groupKey: string | null;
  toolName: string;
};

type ToolGroup<T extends ExecutionMessage> = {
  calls: MessageFragment<T>[];
  results: MessageFragment<T>[];
};

export function isResultMessage(message: ExecutionMessage): boolean {
  return message.additionalProperties?.type === "result";
}

function isFunctionCall(content: ExecutionMessageContent): boolean {
  return content.type === FunctionCallContent;
}

function isFunctionResult(content: ExecutionMessageContent): boolean {
  return content.type === FunctionResultContent;
}

function readCallId(content: ExecutionMessageContent): string | null {
  const callId = content.additionalProperties?.callId;
  return typeof callId === "string" && callId.length > 0 ? callId : null;
}

function readToolName(content: ExecutionMessageContent): string {
  const toolName = content.additionalProperties?.toolName;
  return typeof toolName === "string" ? toolName : "";
}

function cloneMessage<T extends ExecutionMessage>(message: T): T {
  return {
    ...message,
    contents: message.contents.map((content) => ({ ...content })),
  };
}

const messageFragmentCache = new WeakMap<ExecutionMessage, MessageFragment[]>();

export function createMessageFragments<T extends ExecutionMessage>(
  message: T,
): MessageFragment<T>[] {
  const cached = messageFragmentCache.get(message);
  if (cached) {
    return cached as MessageFragment<T>[];
  }

  const fragments: MessageFragment<T>[] = [];

  if (isResultMessage(message)) {
    fragments.push({ type: "result", message, groupKey: null, toolName: "" });
    messageFragmentCache.set(message, fragments);
    return fragments;
  }

  if ((message.role === "user" && !message.author) || message.contents.length === 0) {
    messageFragmentCache.set(message, fragments);
    return fragments;
  }

  let normalContents: ExecutionMessageContent[] = [];
  const flushNormalContents = () => {
    if (normalContents.length === 0) {
      return;
    }

    fragments.push({
      type: "normal",
      message: { ...message, contents: normalContents } as T,
      groupKey: null,
      toolName: "",
    });
    normalContents = [];
  };

  for (const content of message.contents) {
    if (!isFunctionCall(content) && !isFunctionResult(content)) {
      normalContents.push(content);
      continue;
    }

    flushNormalContents();
    const callId = readCallId(content);
    const groupKey =
      callId === null ? null : JSON.stringify([message.streamingScopeId ?? null, callId]);
    fragments.push({
      type: isFunctionCall(content) ? "function-call" : "function-result",
      message: cloneMessage({ ...message, contents: [content] } as T),
      groupKey,
      toolName: readToolName(content),
    });
  }

  flushNormalContents();
  messageFragmentCache.set(message, fragments);
  return fragments;
}

export function processMessages<T extends ExecutionMessage>(
  messages: T[],
): ProcessedMessageItem<T>[] {
  const items: ProcessedMessageItem<T>[] = [];
  const fragments = messages.flatMap(createMessageFragments);

  const toolGroups = new Map<string, ToolGroup<T>>();
  for (const fragment of fragments) {
    if (!fragment.groupKey) {
      continue;
    }

    const group = toolGroups.get(fragment.groupKey) ?? { calls: [], results: [] };
    if (fragment.type === "function-call") {
      group.calls.push(fragment);
    } else if (fragment.type === "function-result") {
      group.results.push(fragment);
    }
    toolGroups.set(fragment.groupKey, group);
  }

  const renderedGroups = new Set<string>();
  for (const fragment of fragments) {
    if (fragment.type === "result") {
      items.push({ type: "result", message: fragment.message });
      continue;
    }

    if (fragment.type === "normal") {
      items.push({ type: "normal", message: fragment.message });
      continue;
    }

    const group = fragment.groupKey ? toolGroups.get(fragment.groupKey) : undefined;
    if (fragment.type === "function-result") {
      if (group && group.calls.length > 0) {
        continue;
      }

      items.push({ type: "normal", message: fragment.message });
      continue;
    }

    if (!fragment.groupKey || !group || group.results.length === 0) {
      items.push({ type: "normal", message: fragment.message });
      continue;
    }

    if (renderedGroups.has(fragment.groupKey)) {
      continue;
    }

    renderedGroups.add(fragment.groupKey);
    items.push({
      type: "accordion",
      messages: [...group.calls, ...group.results].map((item) => item.message),
      toolName: group.calls[0].toolName,
    });
  }

  return items;
}
