export type ChatRouteSessionAction =
  | { type: "clearLocal" }
  | { type: "selectProject"; projectId: string }
  | { type: "hydrate"; hydrateKey: string; projectId: string; taskId: string }
  | { type: "ignore" };

export function getTaskHydrationKey(
  projectId: string | null | undefined,
  taskId: string | null | undefined,
): string | null {
  if (!projectId || !taskId) {
    return null;
  }

  return `${projectId}:${taskId}`;
}

export function getChatRouteSessionAction({
  queryProjectId,
  queryTaskId,
  hydratedTaskKey,
}: {
  queryProjectId: string | null;
  queryTaskId: string | null;
  hydratedTaskKey: string | null;
}): ChatRouteSessionAction {
  if (!queryProjectId) {
    return { type: "clearLocal" };
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
