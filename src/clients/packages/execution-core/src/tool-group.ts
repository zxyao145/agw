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
  callId: string | null;
  groupKey: string | null;
  toolName: string;
};

type ToolGroup<T extends ExecutionMessage> = {
  firstCallIndex: number;
  calls: MessageFragment<T>[];
  results: MessageFragment<T>[];
};

export function isResultMessage(message: ExecutionMessage): boolean {
  return (
    message.additionalProperties?.type === "result" ||
    message.contents.some((content) => content.additionalProperties?.type === "result")
  );
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
  const scopeId = message.streamingScopeId ?? null;

  if (isResultMessage(message)) {
    fragments.push({ type: "result", message, callId: null, groupKey: null, toolName: "" });
    messageFragmentCache.set(message, fragments);
    return fragments;
  }

  if (message.contents.length === 0) {
    fragments.push({ type: "normal", message, callId: null, groupKey: null, toolName: "" });
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
      callId: null,
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
    const groupKey = callId === null ? null : JSON.stringify([scopeId, callId]);
    fragments.push({
      type: isFunctionCall(content) ? "function-call" : "function-result",
      message: cloneMessage({ ...message, contents: [content] } as T),
      callId,
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

  const exactGroups = new Map<string, ToolGroup<T>>();
  const groupsByCallId = new Map<string, ToolGroup<T>[]>();
  const callGroups = new Map<MessageFragment<T>, ToolGroup<T>>();
  // Phase 1: establish every logical Tool use in call order before inspecting results.
  for (const [index, fragment] of fragments.entries()) {
    if (fragment.type !== "function-call" || !fragment.callId || !fragment.groupKey) continue;

    let group = exactGroups.get(fragment.groupKey);
    if (!group) {
      group = {
        firstCallIndex: index,
        calls: [],
        results: [],
      };
      exactGroups.set(fragment.groupKey, group);
      const matchingGroups = groupsByCallId.get(fragment.callId) ?? [];
      matchingGroups.push(group);
      groupsByCallId.set(fragment.callId, matchingGroups);
    }
    group.calls.push(fragment);
    callGroups.set(fragment, group);
  }

  const resultGroups = new Map<MessageFragment<T>, ToolGroup<T>>();
  // Result arrival order is independent. Prefer the exact turn, then recover replayed
  // results whose scope drifted by matching callId to the closest Tool use.
  for (const [index, fragment] of fragments.entries()) {
    if (fragment.type !== "function-result" || !fragment.callId) continue;

    const exactGroup = fragment.groupKey ? exactGroups.get(fragment.groupKey) : undefined;
    const group =
      exactGroup ?? findClosestCallGroup(groupsByCallId.get(fragment.callId) ?? [], index);
    if (!group) continue;

    group.results.push(fragment);
    resultGroups.set(fragment, group);
  }

  const renderedGroups = new Set<ToolGroup<T>>();
  // Phase 2: emit each completed group at its first Tool use position.
  for (const fragment of fragments) {
    if (fragment.type === "result") {
      items.push({ type: "result", message: fragment.message });
      continue;
    }

    if (fragment.type === "normal") {
      items.push({ type: "normal", message: fragment.message });
      continue;
    }

    if (fragment.type === "function-result") {
      if (resultGroups.has(fragment)) continue;

      items.push({ type: "normal", message: fragment.message });
      continue;
    }

    const group = callGroups.get(fragment);
    if (!group || group.results.length === 0) {
      items.push({ type: "normal", message: fragment.message });
      continue;
    }

    if (renderedGroups.has(group)) continue;

    renderedGroups.add(group);
    items.push({
      type: "accordion",
      messages: [...group.calls, ...group.results].map((item) => item.message),
      toolName: group.calls[0].toolName,
    });
  }

  return items;
}

function findClosestCallGroup<T extends ExecutionMessage>(
  candidates: ToolGroup<T>[],
  resultIndex: number,
): ToolGroup<T> | undefined {
  if (candidates.length === 0) return undefined;

  let closestPreceding: ToolGroup<T> | undefined;
  for (const candidate of candidates) {
    if (candidate.firstCallIndex > resultIndex) {
      return closestPreceding ?? candidate;
    }
    closestPreceding = candidate;
  }

  return closestPreceding;
}
