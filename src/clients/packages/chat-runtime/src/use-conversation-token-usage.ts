import * as React from "react";
import { EMPTY_TOKEN_USAGE, type TokenUsage } from "@agw/api";

import {
  conversationTokenUsage,
  type ConversationTokenUsageStore,
} from "./conversation-token-usage";
import type { ExecutionSessionKey } from "./execution-activity-store";

/**
 * 读取对话的 Token usage，用量变化时重新渲染；key 为 null 时为 EMPTY_TOKEN_USAGE。
 * Reads the conversation's token usage and re-renders when it changes; EMPTY_TOKEN_USAGE when key is null.
 */
export function useConversationTokenUsage(
  key: ExecutionSessionKey | null,
  store: ConversationTokenUsageStore = conversationTokenUsage,
): TokenUsage {
  const subscribe = React.useCallback((listener: () => void) => store.subscribe(listener), [store]);
  const getSnapshot = () => (key ? store.get(key) : EMPTY_TOKEN_USAGE);
  return React.useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}
