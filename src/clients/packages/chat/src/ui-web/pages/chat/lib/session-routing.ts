export type ChatRouteSessionAction =
  | { type: "clearLocal" }
  | { type: "selectProject"; projectId: string }
  | {
      type: "hydrateConversation";
      hydrateKey: string;
      projectId: string;
      conversationId: string;
    }
  | { type: "ignore" };

export function getConversationHydrationKey(
  projectId: string | null | undefined,
  conversationId: string | null | undefined,
): string | null {
  if (!projectId || !conversationId) {
    return null;
  }

  return `${projectId}:conversation:${conversationId}`;
}

export function getRouteHydrationKey(action: ChatRouteSessionAction): string | null {
  if (action.type === "hydrateConversation") {
    return action.hydrateKey;
  }

  return null;
}

export function getChatRouteSessionAction({
  queryProjectId,
  queryConversationId,
  hydratedRouteKey,
}: {
  queryProjectId: string | null;
  queryConversationId?: string | null;
  hydratedRouteKey: string | null;
}): ChatRouteSessionAction {
  if (!queryProjectId) {
    return { type: "clearLocal" };
  }

  if (queryConversationId) {
    const hydrateKey = getConversationHydrationKey(queryProjectId, queryConversationId);
    if (hydrateKey && hydratedRouteKey === hydrateKey) {
      return { type: "ignore" };
    }

    return {
      type: "hydrateConversation",
      hydrateKey: hydrateKey!,
      projectId: queryProjectId,
      conversationId: queryConversationId,
    };
  }

  return { type: "selectProject", projectId: queryProjectId };
}
