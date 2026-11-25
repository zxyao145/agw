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

export function getScopePrefix(scope: ConversationComposerScope) {
  return `${STORAGE_KEY_PREFIX}${encodeURIComponent(scope.serverId)}:${encodeURIComponent(scope.projectId)}:`;
}

export type ComposerDraftKey = { conversationId: string } | { draftId: string };

export function keysToClear(scope: ConversationComposerScope, keepDraft?: { draftId: string }) {
  const prefix = getScopePrefix(scope);
  const keep = keepDraft && getStorageKey(scope, keepDraft);
  return (key: string) => key !== keep && key.startsWith(prefix);
}

export function getStorageKey(scope: ConversationComposerScope, key: ComposerDraftKey) {
  const suffix =
    "conversationId" in key
      ? `conversation:${encodeURIComponent(key.conversationId)}`
      : `draft:${encodeURIComponent(key.draftId)}`;
  return getScopePrefix(scope) + suffix;
}

export const conversationComposerStorage = {
  get(scope: ConversationComposerScope, key: ComposerDraftKey): ConversationComposerValues {
    const value = window.localStorage.getItem(getStorageKey(scope, key));
    return value ? (JSON.parse(value) as ConversationComposerValues) : {};
  },
  /**
   * 合并写入；两个字段都为空时删除该对话的记录。
   * Merges the values and removes the conversation's entry once both fields are empty.
   */
  set(
    scope: ConversationComposerScope,
    draftKey: ComposerDraftKey,
    values: ConversationComposerValues,
  ): void {
    const { targetValue, input } = {
      ...conversationComposerStorage.get(scope, draftKey),
      ...values,
    };
    const key = getStorageKey(scope, draftKey);
    if (!targetValue && !input) {
      window.localStorage.removeItem(key);
      return;
    }

    window.localStorage.setItem(
      key,
      JSON.stringify({ targetValue: targetValue || undefined, input: input || undefined }),
    );
  },
  remove(scope: ConversationComposerScope, key: ComposerDraftKey): void {
    window.localStorage.removeItem(getStorageKey(scope, key));
  },
  move(scope: ConversationComposerScope, from: ComposerDraftKey, to: ComposerDraftKey): void {
    conversationComposerStorage.set(scope, to, conversationComposerStorage.get(scope, from));
    if (getStorageKey(scope, from) !== getStorageKey(scope, to))
      conversationComposerStorage.remove(scope, from);
  },
  removeConversations(scope: ConversationComposerScope, keepDraft?: { draftId: string }): void {
    const remove = keysToClear(scope, keepDraft);
    for (let index = window.localStorage.length - 1; index >= 0; index -= 1) {
      const key = window.localStorage.key(index);
      if (key !== null && remove(key)) {
        window.localStorage.removeItem(key);
      }
    }
  },
};
