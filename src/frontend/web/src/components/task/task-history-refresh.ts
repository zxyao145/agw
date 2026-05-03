export type TaskHistoryItem = {
  taskId: string;
};

export function isTaskMissingFromHistory(
  tasks: TaskHistoryItem[],
  currentTaskId: string | null,
): boolean {
  if (!currentTaskId) {
    return false;
  }

  return !tasks.some((task) => task.taskId === currentTaskId);
}
