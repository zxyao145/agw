import * as React from "react";
import type { ConversationRenderItem } from "@agw/chat-core";

/** List-owned expansion survives virtual row unmounts, but never crosses conversations. */
export function useWorkSummary(items: readonly ConversationRenderItem[], conversationKey = "") {
  const [state, setState] = React.useState(() => ({
    conversationKey,
    expandedKeys: new Set<string>(),
  }));
  const summaryKeys = React.useMemo(
    () => new Set(items.filter((item) => item.type === "work-summary").map((item) => item.key)),
    [items],
  );
  const expandedKeys = React.useMemo(
    () =>
      new Set(
        state.conversationKey === conversationKey
          ? [...state.expandedKeys].filter((key) => summaryKeys.has(key))
          : [],
      ),
    [state, conversationKey, summaryKeys],
  );

  // 保留现有 Result 的展开状态，清理已移除区域和其他会话的状态。
  // Preserve expansion for existing Results and discard removed or other-conversation keys.
  React.useEffect(() => {
    setState((current) =>
      current.conversationKey === conversationKey && current.expandedKeys.size === expandedKeys.size
        ? current
        : { conversationKey, expandedKeys },
    );
  }, [conversationKey, expandedKeys]);

  const toggleWorkSummary = React.useCallback(
    (key: string) => {
      setState((current) => {
        const next = new Set(
          current.conversationKey === conversationKey ? current.expandedKeys : [],
        );
        if (next.has(key)) next.delete(key);
        else next.add(key);
        return { conversationKey, expandedKeys: next };
      });
    },
    [conversationKey],
  );
  const rows = React.useMemo(
    () =>
      items.flatMap((item) =>
        item.type === "work-summary" && expandedKeys.has(item.key) ? [item, ...item.items] : [item],
      ),
    [items, expandedKeys],
  );
  return { rows, expandedKeys, toggleWorkSummary };
}
