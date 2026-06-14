export type ChatRouteSessionAction =
  | { type: "clearLocal" }
  | { type: "selectProject"; projectId: string }
  | { type: "hydrate"; hydrateKey: string; projectId: string; taskId: string }
  | {
      type: "hydrateContext";
      hydrateKey: string;
      projectId: string;
      contextId: string;
      taskId: string | null;
    }
  | { type: "ignore" };

export function getTaskHydrationKey(
  projectId: string | null | undefined,
  taskId: string | null | undefined,
): string | null {
  if (!projectId || !taskId) {
    return null;
  }

  return `${projectId}:task:${taskId}`;
}

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
  if (action.type === "hydrate" || action.type === "hydrateContext") {
    return action.hydrateKey;
  }

  return null;
}

export function getChatRouteSessionAction({
  queryProjectId,
  queryTaskId,
  queryContextId,
  hydratedTaskKey,
}: {
  queryProjectId: string | null;
  queryTaskId: string | null;
  queryContextId?: string | null;
  hydratedTaskKey: string | null;
}): ChatRouteSessionAction {
  if (!queryProjectId) {
    return { type: "clearLocal" };
  }

  if (queryContextId) {
    const hydrateKey = getContextHydrationKey(queryProjectId, queryContextId);
    if (hydrateKey && hydratedTaskKey === hydrateKey) {
      return { type: "ignore" };
    }

    return {
      type: "hydrateContext",
      hydrateKey: hydrateKey!,
      projectId: queryProjectId,
      contextId: queryContextId,
      taskId: queryTaskId,
    };
  }

  if (!queryTaskId) {
    return { type: "selectProject", projectId: queryProjectId };
  }

  const hydrateKey = getTaskHydrationKey(queryProjectId, queryTaskId);
  if (hydrateKey && hydratedTaskKey === hydrateKey) {
    return { type: "ignore" };
  }

  return {
    type: "hydrate",
    hydrateKey: hydrateKey!,
    projectId: queryProjectId,
    taskId: queryTaskId,
  };
}
