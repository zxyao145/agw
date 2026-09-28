import type { AiMessage, ConversationHistoryTurn } from "@agw/api";
import { getMessageStreamingScopeId, isUserTurnMessage } from "@agw/execution-core";
import type {
  ConversationMessageRenderItem,
  ConversationRenderItem,
} from "./conversation-render-model";
import { readMessageMeta, readMessageNodeName } from "./message-presentation";

type MessageItem = Extract<ConversationMessageRenderItem, { type: "message" | "plan" }>;

function isTurnInput(item: ConversationMessageRenderItem): item is MessageItem {
  return (
    (item.type === "message" || item.type === "plan") && isUserTurnMessage(item.message.source)
  );
}

function getTurnId(message: AiMessage): string | null {
  const turnId = message.additionalProperties?.turnId;
  return typeof turnId === "string" && turnId.length > 0 ? turnId : null;
}

function getHistoryMessageKey(message: AiMessage): string {
  return JSON.stringify([
    getTurnId(message) ?? getMessageStreamingScopeId(message) ?? null,
    message.additionalProperties?.producerScopeId ?? null,
    message.messageId,
  ]);
}

function getConversationSequence(message: AiMessage): number | null {
  const sequence = message.additionalProperties?.conversationSequence;
  return typeof sequence === "number" && Number.isSafeInteger(sequence) ? sequence : null;
}

export function includeHistoryTurnAnchors(
  messages: readonly AiMessage[],
  turns: readonly ConversationHistoryTurn[],
): AiMessage[] {
  if (turns.length === 0) return [...messages];

  const visibleTurns = new Set(messages.map(getTurnId));
  const anchors = turns
    .filter((turn) => visibleTurns.has(turn.turnId))
    .flatMap((turn) => (turn.input ? [turn.input, ...turn.results] : turn.results));
  const anchorsByKey = new Map(anchors.map((message) => [getHistoryMessageKey(message), message]));
  const present = new Set<string>();
  const output: AiMessage[] = [];
  for (const message of messages) {
    const key = getHistoryMessageKey(message);
    const anchor = anchorsByKey.get(key);
    if (anchor && present.has(key)) continue;
    present.add(key);
    output.push(
      anchor
        ? {
            ...message,
            additionalProperties: {
              ...anchor.additionalProperties,
              ...message.additionalProperties,
            },
          }
        : message,
    );
  }
  for (const anchor of anchors) {
    const key = getHistoryMessageKey(anchor);
    if (present.has(key)) continue;
    const turnId = getTurnId(anchor);
    const sequence = getConversationSequence(anchor);
    const next = output.findIndex((message) => {
      if (getTurnId(message) !== turnId) return false;
      if (isUserTurnMessage(anchor)) return true;
      const messageSequence = getConversationSequence(message);
      return sequence !== null && (messageSequence === null || sequence < messageSequence);
    });
    const index =
      next >= 0 ? next : output.findLastIndex((message) => getTurnId(message) === turnId) + 1;
    output.splice(index, 0, anchor);
    present.add(key);
  }
  return output;
}

/**
 * 找出起点尚未加载的折叠区域，供历史分页使用。
 * Find summaries whose starting boundary has not been loaded from history.
 */
export function getUnloadedWorkSummaryKeys(
  items: readonly ConversationRenderItem[],
  messages: readonly AiMessage[],
): string[] {
  const present = new Set(messages.map(getHistoryMessageKey));
  return items.flatMap((item) =>
    item.type === "work-summary" && !present.has(item.startMessageKey) ? [item.key] : [],
  );
}

function getWorkSummaryName(
  result: AiMessage,
  process: readonly ConversationMessageRenderItem[],
): string | null {
  const nodeName = readMessageNodeName(result);
  if (nodeName) return nodeName;
  const messages = process.flatMap((item) => {
    if (item.type === "message" || item.type === "plan") return [item.message.source];
    if (item.type === "tool-state") return [item.message];
    if (item.type === "tool-accordion") return item.messages.map((message) => message.source);
    return [];
  });
  const nodeNames = new Set(
    messages
      .filter((message) => message.role !== "user")
      .map(readMessageNodeName)
      .filter((name) => name !== null),
  );
  return (nodeNames.size === 1 ? [...nodeNames][0] : null) ?? readMessageMeta(result)?.name ?? null;
}

/**
 * 每个 Result 折叠前一个 Result 或用户输入之后的过程内容。
 * Each Result folds the process following the previous Result or user input.
 */
export function collapseCompletedWork(
  items: readonly ConversationMessageRenderItem[],
): ConversationRenderItem[] {
  const output: ConversationRenderItem[] = [];
  let boundary: MessageItem | Extract<ConversationMessageRenderItem, { type: "result" }> | null =
    null;
  let process: Exclude<ConversationMessageRenderItem, { type: "result" }>[] = [];
  for (const item of items) {
    if (isTurnInput(item)) {
      output.push(...process, item);
      process = [];
      boundary = item;
    } else if (item.type === "result") {
      const hasProcess =
        process.length > 0 ||
        item.message.source.additionalProperties?.hasPrecedingProcessMessages === true;
      if (boundary && hasProcess && !process.some((entry) => entry.type === "human-interaction")) {
        const duration =
          Date.parse(item.message.source.createdAt ?? "") -
          Date.parse(boundary.message.source.createdAt ?? "");
        output.push(
          {
            type: "work-summary",
            key: `work-summary:${getHistoryMessageKey(item.message.source)}`,
            startMessageKey: getHistoryMessageKey(boundary.message.source),
            alignment: "left",
            width: "full",
            name: getWorkSummaryName(item.message.source, process),
            durationMs: Number.isFinite(duration) && duration >= 0 ? duration : null,
            items: process,
          },
          { ...item, hasWorkSummary: true },
        );
      } else {
        output.push(...process, item);
      }
      process = [];
      boundary = item;
    } else {
      process.push(item);
    }
  }
  output.push(...process);
  return output;
}

export function formatWorkedDuration(durationMs: number | null): string {
  if (durationMs === null || !Number.isFinite(durationMs) || durationMs < 0) return "Worked";
  const totalSeconds = Math.floor(durationMs / 1000);
  const seconds = totalSeconds % 60;
  const minutes = Math.floor(totalSeconds / 60) % 60;
  const hours = Math.floor(totalSeconds / 3600);
  const duration = hours
    ? `${hours}h ${minutes}m ${seconds}s`
    : minutes
      ? `${minutes}m ${seconds}s`
      : `${seconds}s`;
  return `Worked for ${duration}`;
}
