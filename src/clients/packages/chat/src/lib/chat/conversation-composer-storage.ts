/**
 * 每个对话的本地输入配置：执行目标与输入草稿。
 * Per-conversation local composer settings: the execution target and the input draft.
 */
export interface ConversationComposerValues {
  targetValue?: string;
  input?: string;
}

/**
 * 本地配置所属的 Server 与项目。
 * The server and project that own the local settings.
 */
export interface ConversationComposerScope {
  serverId: string;
  projectId: string;
}

const STORAGE_KEY_PREFIX = "agw:chat-conversation-composer:";

function getScopePrefix(scope: ConversationComposerScope) {
  return `${STORAGE_KEY_PREFIX}${encodeURIComponent(scope.serverId)}:${encodeURIComponent(scope.projectId)}:`;
}

// conversationId 为 null 表示尚未发送的新对话。
// A null conversationId stands for the new conversation that has not been sent yet.
function getStorageKey(scope: ConversationComposerScope, conversationId: string | null) {
  return conversationId === null
    ? `${getScopePrefix(scope)}new`
    : `${getScopePrefix(scope)}conversation:${encodeURIComponent(conversationId)}`;
}

export const conversationComposerStorage = {
  get(scope: ConversationComposerScope, conversationId: string | null): ConversationComposerValues {
    const value = window.localStorage.getItem(getStorageKey(scope, conversationId));
    return value ? (JSON.parse(value) as ConversationComposerValues) : {};
  },
  /**
   * 合并写入；两个字段都为空时删除该对话的记录。
   * Merges the values and removes the conversation's entry once both fields are empty.
   */
  set(
    scope: ConversationComposerScope,
    conversationId: string | null,
    values: ConversationComposerValues,
  ): void {
    const { targetValue, input } = {
      ...conversationComposerStorage.get(scope, conversationId),
      ...values,
    };
    const key = getStorageKey(scope, conversationId);
    if (!targetValue && !input) {
      window.localStorage.removeItem(key);
      return;
    }

    window.localStorage.setItem(
      key,
      JSON.stringify({ targetValue: targetValue || undefined, input: input || undefined }),
    );
  },
  remove(scope: ConversationComposerScope, conversationId: string | null): void {
    window.localStorage.removeItem(getStorageKey(scope, conversationId));
  },
  /**
   * 删除项目内所有已有对话的记录，保留新对话的记录。
   * Removes the entries of every existing conversation in the project and keeps the new conversation's entry.
   */
  removeConversations(scope: ConversationComposerScope): void {
    const prefix = `${getScopePrefix(scope)}conversation:`;
    for (let index = window.localStorage.length - 1; index >= 0; index -= 1) {
      const key = window.localStorage.key(index);
      if (key?.startsWith(prefix)) {
        window.localStorage.removeItem(key);
      }
    }
  },
};
