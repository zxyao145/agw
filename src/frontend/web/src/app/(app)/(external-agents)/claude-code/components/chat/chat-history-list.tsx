"use client";

import * as React from "react";
import { Trash2, Plus, Edit2, Check, X, Info, RotateCw } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  deleteTaskById,
  updateTaskTitle,
  deleteAllTasks,
  getAllTasks,
  type TaskSummary,
} from "@/api/task-client";
import { cn } from "@/lib/utils";
import { CLAUDE_CODE_PROJECT_ID } from "../../contants";

interface ChatHistoryListProps {
  currentTaskId: string | null;
  onTaskSelect: (taskId: string) => void;
  onNewChat: () => void;
  onTaskDeleted: (taskId: string) => void;
  onAllSessionsCleared: () => void;
}

export function ChatHistoryList({
  currentTaskId,
  onTaskSelect,
  onNewChat,
  onTaskDeleted,
  onAllSessionsCleared,
}: ChatHistoryListProps) {
  const [tasks, setTasks] = React.useState<TaskSummary[]>([]);
  const [editingTaskId, setEditingTaskId] = React.useState<string | null>(null);
  const [editTitle, setEditTitle] = React.useState("");
  const [infoModalOpen, setInfoModalOpen] = React.useState(false);
  const [isRefreshing, setIsRefreshing] = React.useState(false);

  const refreshTasks = React.useCallback(async () => {
    setIsRefreshing(true);
    try {
      const latestTasks = await getAllTasks(CLAUDE_CODE_PROJECT_ID);
      setTasks(latestTasks);
    } catch (error) {
      console.error("Failed to load chat history:", error);
    } finally {
      setIsRefreshing(false);
    }
  }, []);

  React.useEffect(() => {
    void refreshTasks();
  }, [refreshTasks]);

  const handleDelete = async (task: TaskSummary, e: React.MouseEvent) => {
    e.stopPropagation();

    // if (!confirm("Are you sure you want to delete this chat?")) {
    //   return;
    // }

    try {
      const success = await deleteTaskById(task.taskId, CLAUDE_CODE_PROJECT_ID);
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
      await deleteAllTasks(CLAUDE_CODE_PROJECT_ID);
      toast.success("All chats cleared");
      onAllSessionsCleared();
      await refreshTasks();
    } catch (error) {
      console.error("Clear all error:", error);
      toast.error("Error clearing chats");
    }
  };

  const startEditing = (task: TaskSummary, e: React.MouseEvent) => {
    e.stopPropagation();
    setEditingTaskId(task.taskId);
    setEditTitle(task.title);
  };

  const cancelEditing = (e: React.SyntheticEvent<HTMLElement>) => {
    e.stopPropagation();
    setEditingTaskId(null);
    setEditTitle("");
  };

  const saveEdit = async (taskId: string, e: React.SyntheticEvent<HTMLElement>) => {
    e.stopPropagation();

    if (!editTitle.trim()) {
      toast.error("Title cannot be empty");
      return;
    }

    try {
      const success = await updateTaskTitle(taskId, editTitle.trim(), CLAUDE_CODE_PROJECT_ID);
      if (success) {
        toast.success("Title updated");
        setEditingTaskId(null);
        setEditTitle("");
        await refreshTasks();
      } else {
        toast.error("Failed to update title");
      }
    } catch (error) {
      console.error("Update title error:", error);
      toast.error("Error updating title");
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
        <div>
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
              await Promise.resolve(onNewChat());
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
        </div>
      </div>

      {/* Session list */}
      <div className="flex-1 overflow-y-auto p-2 space-y-1">
        {tasks.length === 0 ? (
          <div className="text-center py-8 text-muted-foreground text-sm">No chat history yet</div>
        ) : (
          tasks.map((task) => {
            const isActive = task.taskId === currentTaskId;
            const isEditing = editingTaskId === task.taskId;

            return (
              <div
                key={task.taskId}
                onClick={() => !isEditing && onTaskSelect(task.taskId)}
                className={cn(
                  "group p-3 rounded-md cursor-pointer transition-colors border",
                  isActive ? "bg-blue-50" : "bg-card hover:bg-accent/50 border-transparent",
                )}
              >
                <div className="flex items-start gap-2">
                  <div className="flex-1 min-w-0">
                    {isEditing ? (
                      <div className="flex items-center gap-1" onClick={(e) => e.stopPropagation()}>
                        <input
                          type="text"
                          value={editTitle}
                          onChange={(e) => setEditTitle(e.target.value)}
                          className="flex-1 px-2 py-1 text-sm border rounded bg-background"
                          autoFocus
                          onKeyDown={(e) => {
                            if (e.key === "Enter") {
                              saveEdit(task.taskId, e);
                            } else if (e.key === "Escape") {
                              cancelEditing(e);
                            }
                          }}
                        />
                        <Button
                          size="sm"
                          variant="ghost"
                          className="h-6 w-6 p-0"
                          onClick={(e) => saveEdit(task.taskId, e)}
                        >
                          <Check className="h-3 w-3" />
                        </Button>
                        <Button
                          size="sm"
                          variant="ghost"
                          className="h-6 w-6 p-0"
                          onClick={cancelEditing}
                        >
                          <X className="h-3 w-3" />
                        </Button>
                      </div>
                    ) : (
                      <>
                        <div className="font-medium text-sm truncate">{task.title}</div>
                        <div className="mt-1 text-xs text-muted-foreground">
                          {formatDate(task.updateTime ?? task.createTime)}
                        </div>
                      </>
                    )}
                  </div>
                  {!isEditing && (
                    <div className="flex items-center gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                      <Button
                        size="sm"
                        variant="ghost"
                        className="h-6 w-6 p-0"
                        onClick={(e) => startEditing(task, e)}
                      >
                        <Edit2 className="h-3 w-3" />
                      </Button>
                      <Button
                        size="sm"
                        variant="ghost"
                        className="h-6 w-6 p-0 text-destructive"
                        onClick={(e) => handleDelete(task, e)}
                      >
                        <Trash2 className="h-3 w-3" />
                      </Button>
                    </div>
                  )}
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
