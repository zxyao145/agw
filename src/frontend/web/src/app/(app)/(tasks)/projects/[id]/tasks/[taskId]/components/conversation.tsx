import { ProjectTaskDto } from "../types";


function formatDate(value?: string | null): string {
  if (!value) return "-";
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return value;
  return d.toLocaleString();
}

export const Conversation = ({ task }: { task: ProjectTaskDto }) => {
  return (
    <div className="flex items-center gap-2 text-xs text-muted-foreground">
      <span>Conversation</span>
      {task.createTime && (
        <>
          <span>•</span>
          <span>Created: {formatDate(task.createTime)}</span>
        </>
      )}
      {task.startedTime && (
        <>
          <span>•</span>
          <span>Started: {formatDate(task.startedTime)}</span>
        </>
      )}
      {task.finishedTime && (
        <>
          <span>•</span>
          <span>Completed: {formatDate(task.finishedTime)}</span>
        </>
      )}
    </div>
  );
};
