"use client";

import * as React from "react";
import { MessageSquare, Trash2, Plus, Edit2, Check, X, Info } from "lucide-react";
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
  subscribeToSessions,
  deleteSession,
  deleteRemoteSessionResources,
  updateSessionTitle,
  clearAllSessions,
  getAllSessions,
} from "../../lib/chat-history-service";
import type { ChatSessionDocument } from "../../lib/chat-history-db";
import { cn } from "@/lib/utils";

interface ChatHistoryListProps {
  currentThreadId: string | null;
  onSessionSelect: (session: ChatSessionDocument) => void;
  onNewChat: () => void;
  onSessionDeleted: (threadId: string) => void;
  onAllSessionsCleared: () => void;
}

export function ChatHistoryList({
  currentThreadId,
  onSessionSelect,
  onNewChat,
  onSessionDeleted,
  onAllSessionsCleared,
}: ChatHistoryListProps) {
  const [sessions, setSessions] = React.useState<ChatSessionDocument[]>([]);
  const [editingSessionId, setEditingSessionId] = React.useState<string | null>(null);
  const [editTitle, setEditTitle] = React.useState("");
  const [infoModalOpen, setInfoModalOpen] = React.useState(false);

  // Subscribe to session changes
  React.useEffect(() => {
    let isMounted = true;

    // Load initial sessions immediately
    const loadInitialSessions = async () => {
      try {
        const initialSessions = await getAllSessions();
        if (isMounted) {
          setSessions(initialSessions);
        }
      } catch (error) {
        console.error('Failed to load chat history:', error);
      }
    };

    loadInitialSessions();

    // Subscribe to subsequent changes
    const unsubscribe = subscribeToSessions((newSessions) => {
      if (isMounted) {
        setSessions(newSessions);
      }
    });

    return () => {
      isMounted = false;
      unsubscribe();
    };
  }, []);

  const handleDelete = async (
    session: ChatSessionDocument,
    e: React.MouseEvent
  ) => {
    e.stopPropagation();

    if (!confirm("Are you sure you want to delete this chat?")) {
      return;
    }

    try {
      const remoteDeleted = await deleteRemoteSessionResources(session.threadId);
      if (!remoteDeleted) {
        toast.error("Failed to delete working directory");
        return;
      }
      const success = await deleteSession(session._id);
      if (success) {
        toast.success("Chat deleted successfully");
        if (session.threadId === currentThreadId) {
          onSessionDeleted(session.threadId);
        }
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
      const results = await Promise.allSettled(
        sessions.map((session) => deleteRemoteSessionResources(session.threadId)),
      );
      const failed = results.filter(
        (result) => result.status === "rejected" || result.value === false,
      );
      if (failed.length > 0) {
        toast.error("Failed to delete one or more working directories");
        return;
      }
      await clearAllSessions();
      toast.success("All chats cleared");
      onAllSessionsCleared();
    } catch (error) {
      console.error("Clear all error:", error);
      toast.error("Error clearing chats");
    }
  };

  const startEditing = (session: ChatSessionDocument, e: React.MouseEvent) => {
    e.stopPropagation();
    setEditingSessionId(session._id);
    setEditTitle(session.title);
  };

  const cancelEditing = (e: React.MouseEvent) => {
    e.stopPropagation();
    setEditingSessionId(null);
    setEditTitle("");
  };

  const saveEdit = async (sessionId: string, e: React.MouseEvent) => {
    e.stopPropagation();

    if (!editTitle.trim()) {
      toast.error("Title cannot be empty");
      return;
    }

    try {
      const success = await updateSessionTitle(sessionId, editTitle.trim());
      if (success) {
        toast.success("Title updated");
        setEditingSessionId(null);
        setEditTitle("");
      } else {
        toast.error("Failed to update title");
      }
    } catch (error) {
      console.error("Update title error:", error);
      toast.error("Error updating title");
    }
  };

  const formatDate = (timestamp: number) => {
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

  const formatSize = (bytes: number) => {
    if(!bytes){
      return "0 B";
    }
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  };

  const totalSize = sessions.reduce((sum, s) => sum + s.size, 0);

  return (
    <div className="flex flex-col bg-muted/30 w-full">
      {/* Header */}
      <div className="p-4 border-b flex items-center justify-between">
        <h2 className="font-semibold text-sm">Chat History</h2>
        <div>
          <Button
            className="cursor-pointer"
            size="sm"
            variant="ghost"
            onClick={onNewChat}
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
        {sessions.length === 0 ? (
          <div className="text-center py-8 text-muted-foreground text-sm">
            No chat history yet
          </div>
        ) : (
          sessions.map((session) => {
            const isActive = session.threadId === currentThreadId;
            const isEditing = editingSessionId === session._id;

            return (
              <div
                key={session._id}
                onClick={() => !isEditing && onSessionSelect(session)}
                className={cn(
                  "group p-3 rounded-md cursor-pointer transition-colors border",
                  isActive
                    ? "bg-blue-50"
                    : "bg-card hover:bg-accent/50 border-transparent",
                )}
              >
                <div className="flex items-start gap-2">
                  <div className="flex-1 min-w-0">
                    {isEditing ? (
                      <div
                        className="flex items-center gap-1"
                        onClick={(e) => e.stopPropagation()}
                      >
                        <input
                          type="text"
                          value={editTitle}
                          onChange={(e) => setEditTitle(e.target.value)}
                          className="flex-1 px-2 py-1 text-sm border rounded bg-background"
                          autoFocus
                          onKeyDown={(e) => {
                            if (e.key === "Enter") {
                              saveEdit(session._id, e as any);
                            } else if (e.key === "Escape") {
                              cancelEditing(e as any);
                            }
                          }}
                        />
                        <Button
                          size="sm"
                          variant="ghost"
                          className="h-6 w-6 p-0"
                          onClick={(e) => saveEdit(session._id, e)}
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
                        <div className="font-medium text-sm truncate">
                          {session.title}
                        </div>
                        <div className="flex items-center gap-2 mt-1 text-xs text-muted-foreground">
                          <span>{formatDate(session.createdAt)}</span>
                          <span>•</span>
                          <span>{session.messages?.length ?? 0} msgs</span>
                          <span>•</span>
                          <span>{formatSize(session.size)}</span>
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
                        onClick={(e) => startEditing(session, e)}
                      >
                        <Edit2 className="h-3 w-3" />
                      </Button>
                      <Button
                        size="sm"
                        variant="ghost"
                        className="h-6 w-6 p-0 text-destructive"
                        onClick={(e) => handleDelete(session, e)}
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
        <DialogContent className="sm:max-w-md">
          <DialogHeader>
            <DialogTitle>Chat History Storage</DialogTitle>
            <DialogDescription>
              Storage statistics and management options
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-4 py-4">
            <div className="space-y-2">
              <div className="flex justify-between text-sm">
                <span className="text-muted-foreground">Total sessions:</span>
                <span className="font-mono font-medium">
                  {sessions.length} / 1000
                </span>
              </div>
              <div className="flex justify-between text-sm">
                <span className="text-muted-foreground">Storage used:</span>
                <span className="font-mono font-medium">
                  {formatSize(totalSize)} / 200 MB
                </span>
              </div>
            </div>

            {sessions.length > 0 && (
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
              <p className="mb-2">Storage limits:</p>
              <ul className="space-y-1 list-disc list-inside">
                <li>Maximum 1000 chat sessions</li>
                <li>Maximum 200 MB total storage</li>
                <li>Oldest sessions are automatically deleted when limits are exceeded</li>
              </ul>
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
