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

/** Project completed turns without changing tool matching, message identity, or source data. */
export function collapseCompletedWork(
  items: readonly ConversationMessageRenderItem[],
  isCurrentTurnActive: boolean,
): ConversationRenderItem[] {
  const output: ConversationRenderItem[] = [];
  let start = 0;

  const appendTurn = (end: number, active: boolean) => {
    const turn = items.slice(start, end);
    const input = turn[0];
    const results = turn.filter((item) => item.type === "result");
    const process = turn.slice(1).filter((item) => item.type !== "result");
    if (
      !input ||
      !isTurnInput(input) ||
      active ||
      results.length === 0 ||
      process.length === 0 ||
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
      key: `work-summary:${input.key}`,
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
