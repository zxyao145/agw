export type ChatRouteSessionAction =
  | { type: "clearLocal" }
  | { type: "selectProject"; projectId: string }
  | {
      type: "hydrateContext";
      hydrateKey: string;
      projectId: string;
      contextId: string;
    }
  | { type: "ignore" };

export function getContextHydrationKey(
  projectId: string | null | undefined,
  contextId: string | null | undefined,
): string | null {
  if (!projectId || !contextId) {
    return null;
  }

  return `${projectId}:context:${contextId}`;
}

export function getRouteHydrationKey(action: ChatRouteSessionAction): string | null {
  if (action.type === "hydrateContext") {
    return action.hydrateKey;
  }

  return null;
}

export function getChatRouteSessionAction({
  queryProjectId,
  queryContextId,
  hydratedRouteKey,
}: {
  queryProjectId: string | null;
  queryContextId?: string | null;
  hydratedRouteKey: string | null;
}): ChatRouteSessionAction {
  if (!queryProjectId) {
    return { type: "clearLocal" };
  }

  if (queryContextId) {
    const hydrateKey = getContextHydrationKey(queryProjectId, queryContextId);
    if (hydrateKey && hydratedRouteKey === hydrateKey) {
      return { type: "ignore" };
    }

    return {
      type: "hydrateContext",
      hydrateKey: hydrateKey!,
      projectId: queryProjectId,
      contextId: queryContextId,
    };
  }

  return { type: "selectProject", projectId: queryProjectId };
}
