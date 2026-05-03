"use client";

import * as React from "react";
import { Trash2, Plus, Info, RotateCw } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { deleteTaskById, deleteAllTasks, getAllTasks, type TaskSummary } from "@/api/task-client";
import { cn } from "@/lib/utils";
import { isTaskMissingFromHistory } from "./task-history-refresh";

interface TaskHistoryListProps {
  projectId: string;
  currentTaskId: string | null;
  onTaskSelect: (taskId: string) => void;
  onNewTask: () => void;
  onTaskDeleted: (taskId: string) => void;
  onAllTasksDeleted: () => void;
  headerActions?: React.ReactNode;
}

export function TaskHistoryList({
  projectId,
  currentTaskId,
  onTaskSelect,
  onNewTask,
  onTaskDeleted,
  onAllTasksDeleted,
  headerActions,
}: TaskHistoryListProps) {
  const [tasks, setTasks] = React.useState<TaskSummary[]>([]);
  const [infoModalOpen, setInfoModalOpen] = React.useState(false);
  const [isRefreshing, setIsRefreshing] = React.useState(false);

  const refreshTasks = React.useCallback(async (): Promise<TaskSummary[]> => {
    setIsRefreshing(true);
    try {
      if (!projectId) {
        setTasks([]);
        return [];
      }
      const latestTasks = await getAllTasks(projectId);
      setTasks(latestTasks);
      return latestTasks;
    } catch (error) {
      console.error("Failed to load chat history:", error);
      return [];
    } finally {
      setIsRefreshing(false);
    }
  }, [projectId]);

  React.useEffect(() => {
    void refreshTasks();
  }, [refreshTasks]);

  React.useEffect(() => {
    if (!projectId || !currentTaskId) {
      return;
    }

    let cancelled = false;
    let timeoutId: ReturnType<typeof setTimeout> | null = null;
    let attempts = 0;

    const refreshUntilCurrentTaskAppears = async () => {
      attempts += 1;
      const latestTasks = await refreshTasks();
      if (
        cancelled ||
        attempts >= 5 ||
        !isTaskMissingFromHistory(latestTasks, currentTaskId)
      ) {
        return;
      }

      timeoutId = setTimeout(() => {
        void refreshUntilCurrentTaskAppears();
      }, 500);
    };

    void refreshUntilCurrentTaskAppears();

    return () => {
      cancelled = true;
      if (timeoutId) {
        clearTimeout(timeoutId);
      }
    };
  }, [currentTaskId, projectId, refreshTasks]);

  const handleDelete = async (task: TaskSummary, e: React.MouseEvent) => {
    e.stopPropagation();

    // if (!confirm("Are you sure you want to delete this chat?")) {
    //   return;
    // }

    try {
      const success = await deleteTaskById(task.taskId, projectId);
      if (success) {
        toast.success("Chat deleted successfully");
        if (task.taskId === currentTaskId) {
          onTaskDeleted(task.taskId);
        }
        await refreshTasks();
      } else {
        toast.error("Failed to delete chat");
      }
    } catch (error) {
      console.error("Delete error:", error);
      toast.error("Error deleting chat");
    }
  };

  const handleClearAll = async () => {
    if (!confirm("Are you sure you want to delete all chat history?")) {
      return;
    }

    try {
      await deleteAllTasks(projectId);
      toast.success("All chats cleared");
      onAllTasksDeleted();
      await refreshTasks();
    } catch (error) {
      console.error("Clear all error:", error);
      toast.error("Error clearing chats");
    }
  };

  const formatDate = (timestamp: string) => {
    const date = new Date(timestamp);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    const diffHours = Math.floor(diffMs / 3600000);
    // const diffDays = Math.floor(diffMs / 86400000);

    if (diffMins < 1) return "Just now";
    if (diffMins < 60) return `${diffMins}m ago`;
    if (diffHours < 24) return `${diffHours}h ago`;
    // if (diffDays < 7) return `${diffDays}d ago`;

    return date.toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
    });
  };

  return (
    <div className="flex flex-col bg-muted/30 w-full h-full min-h-0">
      {/* Header */}
      <div className="p-4 border-b flex items-center justify-between">
        <h2 className="font-semibold text-sm">Chat History</h2>
        <div className="tools">
          <Button
            className="cursor-pointer"
            size="sm"
            variant="ghost"
            onClick={refreshTasks}
            disabled={isRefreshing}
            aria-label="Refresh chat history"
          >
            <RotateCw
              className={cn("h-4 w-4", isRefreshing && "animate-spin text-muted-foreground")}
            />
          </Button>
          <Button
            className="cursor-pointer"
            size="sm"
            variant="ghost"
            onClick={async () => {
              await Promise.resolve(onNewTask());
              await refreshTasks();
            }}
          >
            {/* NewChat */}
            <Plus className="h-4 w-4" />
          </Button>
          <Button
            className="cursor-pointer"
            size="sm"
            variant="ghost"
            onClick={() => setInfoModalOpen(true)}
          >
            <Info className="h-4 w-4 " />
          </Button>
          {headerActions}
        </div>
      </div>

      {/* Session list */}
      <div className="flex-1 overflow-y-auto p-2 space-y-1">
        {tasks.length === 0 ? (
          <div className="text-center py-8 text-muted-foreground text-sm">No chat history yet</div>
        ) : (
          tasks.map((task) => {
            const isActive = task.taskId === currentTaskId;

            return (
              <div
                key={task.taskId}
                onClick={() => onTaskSelect(task.taskId)}
                className={cn(
                  "group p-3 rounded-md cursor-pointer transition-colors border",
                  isActive ? "bg-blue-50" : "bg-card hover:bg-accent/50 border-transparent",
                )}
              >
                <div className="flex items-start gap-2">
                  <div className="flex-1 min-w-0">
                    <div className="font-medium text-sm truncate">{task.title}</div>
                    <div className="mt-1 text-xs text-muted-foreground">
                      {formatDate(task.updateTime ?? task.createTime)}
                    </div>
                  </div>
                  <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                    <Button
                      size="sm"
                      variant="ghost"
                      className="h-6 w-6 p-0 text-destructive"
                      onClick={(e) => handleDelete(task, e)}
                    >
                      <Trash2 className="h-3 w-3" />
                    </Button>
                  </div>
                </div>
              </div>
            );
          })
        )}
      </div>

      {/* Info Modal */}
      <Dialog open={infoModalOpen} onOpenChange={setInfoModalOpen}>
        <DialogContent size="sm">
          <DialogHeader>
            <DialogTitle>Chat History Storage</DialogTitle>
            <DialogDescription>Storage statistics and management options</DialogDescription>
          </DialogHeader>
          <div className="space-y-4 py-4">
            <div className="space-y-2">
              <div className="flex justify-between text-sm">
                <span className="text-muted-foreground">Total tasks:</span>
                <span className="font-mono font-medium">{tasks.length}</span>
              </div>
            </div>

            {tasks.length > 0 && (
              <Button
                variant="destructive"
                className="w-full"
                onClick={async () => {
                  await handleClearAll();
                  setInfoModalOpen(false);
                }}
              >
                Clear All History
              </Button>
            )}

            <div className="text-xs text-muted-foreground border-t pt-4">
              <p className="mb-2">Chat history is stored on the server.</p>
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
