import type { AiMessage, ConversationHistoryTurn } from "@agw/api";
import { isUserTurnMessage } from "@agw/execution-core";
import type {
  ConversationMessageRenderItem,
  ConversationRenderItem,
} from "./conversation-render-model";

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

export function includeHistoryTurnAnchors(
  messages: readonly AiMessage[],
  turns: readonly ConversationHistoryTurn[],
): AiMessage[] {
  if (turns.length === 0) return [...messages];

  const turnsById = new Map(turns.map((turn) => [turn.turnId, turn]));
  const resultIdsByTurn = new Map(
    turns.map((turn) => [turn.turnId, new Set(turn.results.map((result) => result.messageId))]),
  );
  const firstIndex = new Map<string, number>();
  const lastIndex = new Map<string, number>();
  const present = new Set(messages.map((message) => `${getTurnId(message)}:${message.messageId}`));
  for (const [index, message] of messages.entries()) {
    const turnId = getTurnId(message);
    if (!turnId || !turnsById.has(turnId)) continue;
    if (!firstIndex.has(turnId)) firstIndex.set(turnId, index);
    lastIndex.set(turnId, index);
  }

  const output: AiMessage[] = [];
  for (const [index, message] of messages.entries()) {
    const turnId = getTurnId(message);
    const turn = turnId ? turnsById.get(turnId) : undefined;
    if (turn && firstIndex.get(turnId!) === index && turn.input) {
      const key = `${turnId}:${turn.input.messageId}`;
      if (!present.has(key)) output.push(turn.input);
    }
    if (!turnId || !resultIdsByTurn.get(turnId)?.has(message.messageId)) output.push(message);
    if (turn && lastIndex.get(turnId!) === index) {
      for (const result of turn.results) {
        output.push(result);
      }
    }
  }
  return output;
}

/** Project completed turns without changing tool matching, message identity, or source data. */
export function collapseCompletedWork(
  items: readonly ConversationMessageRenderItem[],
  isCurrentTurnActive: boolean,
  turns: readonly ConversationHistoryTurn[] = [],
): ConversationRenderItem[] {
  const output: ConversationRenderItem[] = [];
  const turnsById = new Map(turns.map((turn) => [turn.turnId, turn]));
  let start = 0;

  const appendTurn = (end: number, active: boolean) => {
    const turn = items.slice(start, end);
    const input = turn[0];
    const results = turn.filter((item) => item.type === "result");
    const process = turn.slice(1).filter((item) => item.type !== "result");
    const turnId =
      input && (input.type === "message" || input.type === "plan")
        ? getTurnId(input.message.source)
        : null;
    const historyTurn = turnId ? turnsById.get(turnId) : undefined;
    if (
      !input ||
      !isTurnInput(input) ||
      active ||
      results.length === 0 ||
      (process.length === 0 && !historyTurn?.hasProcessMessages) ||
      historyTurn?.status === "accepted" ||
      historyTurn?.status === "running" ||
      process.some((item) => item.type === "human-interaction")
    ) {
      for (const item of turn) output.push(item);
      return;
    }

    const startedAt = Date.parse(input.message.source.createdAt ?? "");
    const endedAt = Date.parse(results.at(-1)!.message.source.createdAt ?? "");
    const duration = endedAt - startedAt;
    output.push(input, {
      type: "work-summary",
      key: `work-summary:${turnId ?? input.key}`,
      alignment: "left",
      width: "full",
      durationMs: Number.isFinite(duration) && duration >= 0 ? duration : null,
      items: process,
    });
    for (const [index, result] of results.entries()) {
      output.push(index === 0 ? { ...result, hasWorkSummary: true } : result);
    }
  };

  for (let index = 0; index < items.length; index += 1) {
    if (isTurnInput(items[index])) {
      appendTurn(index, false);
      start = index;
    }
  }
  appendTurn(items.length, isCurrentTurnActive);
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
