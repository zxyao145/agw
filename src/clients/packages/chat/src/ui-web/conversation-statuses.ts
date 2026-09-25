"use client";

import * as React from "react";
import { toast } from "sonner";

import { getApiErrorMessage } from "@agw/api";
import { executionSessionManager, type ConversationStatus } from "@agw/chat-runtime";
import { getConversationActivity } from "@agw/projects";
import { ConversationStatusSnapshots } from "../lib/chat/conversation-status-snapshots";

const EMPTY_STATUSES: ReadonlyMap<string, ConversationStatus> = new Map();

function reportSnapshotError(error: unknown): void {
  toast.error(`Failed to load conversation status: ${getApiErrorMessage(error)}`);
}

export type ConversationStatusesOptions = {
  serverId: string;
  projectId: string | null;
  conversationId: string | null;
};

/**
 * 返回当前项目的会话状态。进入页面、切换项目、当前会话 ID 变化、任一执行连接重连成功，以及收到本项目已被更新 Turn 取代的生命周期消息时获取快照；Turn 事件由 ExecutionSessionManager 写入同一个状态库。
 * Returns the current project's conversation statuses. Snapshots are fetched on entering the page, switching projects, current conversation ID changes, any execution reconnection and this project's lifecycle messages superseded by a newer turn; ExecutionSessionManager writes turn events into the same store.
 */
export function useConversationStatuses({
  serverId,
  projectId,
  conversationId,
}: ConversationStatusesOptions): ReadonlyMap<string, ConversationStatus> {
  const store = executionSessionManager.conversationStatuses;
  const snapshotsRef = React.useRef<ConversationStatusSnapshots | null>(null);
  const observedConversationIdRef = React.useRef(conversationId);

  React.useEffect(() => {
    const snapshots = new ConversationStatusSnapshots(
      store,
      getConversationActivity,
      reportSnapshotError,
    );
    snapshotsRef.current = snapshots;
    return () => {
      snapshots.dispose();
      if (snapshotsRef.current === snapshots) snapshotsRef.current = null;
    };
  }, [store]);

  React.useEffect(() => {
    if (!projectId) return;
    const scope = { serverId, projectId };
    const refresh = () => void snapshotsRef.current?.refresh(scope);
    refresh();
    const unsubscribeReconnected = executionSessionManager.subscribeReconnected(refresh);
    const unsubscribeSuperseded = executionSessionManager.subscribeSupersededTurn((key) => {
      if (key.serverId === serverId && key.projectId === projectId) refresh();
    });
    return () => {
      unsubscribeReconnected();
      unsubscribeSuperseded();
    };
  }, [projectId, serverId]);

  React.useEffect(() => {
    if (observedConversationIdRef.current === conversationId) return;
    observedConversationIdRef.current = conversationId;
    // 打开已有会话或新会话被受理时更新快照；切换项目由上面的请求覆盖。
    // Refresh when an existing conversation opens or a new one is accepted; project switches use the request above.
    if (projectId && conversationId) void snapshotsRef.current?.refresh({ serverId, projectId });
  }, [conversationId, projectId, serverId]);

  const getSnapshot = React.useCallback(
    () => (projectId ? store.getStatuses({ serverId, projectId }) : EMPTY_STATUSES),
    [projectId, serverId, store],
  );
  return React.useSyncExternalStore(store.subscribe, getSnapshot, getSnapshot);
}
