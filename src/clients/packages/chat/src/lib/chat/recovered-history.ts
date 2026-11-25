import type { AiMessage } from "@agw/api";
import {
  getMessageStreamingScopeId,
  getStreamingIdentity,
  replaceStreamingScope,
} from "@agw/execution-core";

export function liveMessageScope(message: AiMessage, activeScope: string | null) {
  const turn = message.additionalProperties?.turnId;
  const turnId = typeof turn === "string" && turn.trim() ? turn : null;
  return getMessageStreamingScopeId(message) ?? turnId ?? activeScope ?? message.messageId;
}

/** 合并历史和恢复期间的消息。Complete live turns include their input and all streaming fragments. */
export function mergeRecoveredHistory(
  history: AiMessage[],
  live: AiMessage[],
  completeTurns: ReadonlySet<string>,
) {
  let result = history;
  for (const turnId of completeTurns) {
    const messages = live.filter((message) => getMessageStreamingScopeId(message) === turnId);
    if (!messages.length) continue;
    const liveKeys = new Set(messages.map(getStreamingIdentity));
    const preceding = history.filter(
      (message) =>
        getMessageStreamingScopeId(message) === turnId &&
        !liveKeys.has(getStreamingIdentity(message)),
    );
    result = replaceStreamingScope(result, [...preceding, ...messages], turnId);
  }
  const existing = new Set(result.map(getStreamingIdentity));
  return [...result, ...live.filter((message) => !existing.has(getStreamingIdentity(message)))];
}
