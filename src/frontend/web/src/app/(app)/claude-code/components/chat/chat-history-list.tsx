"use client";

import * as React from "react";
import { Trash2, Plus, Edit2, Check, X, Info } from "lucide-react";
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
  deleteSessionByThreadId,
  updateSessionTitle,
  clearAllSessions,
  getAllSessions,
  type ChatSessionRecordSummary,
} from "../../lib/chat-history-service";
import { cn } from "@/lib/utils";

interface ChatHistoryListProps {
  currentThreadId: string | null;
  onSessionSelect: (sessionId: string) => void;
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
  const [sessions, setSessions] = React.useState<ChatSessionRecordSummary[]>([]);
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
    session: ChatSessionRecordSummary,
    e: React.MouseEvent
  ) => {
    e.stopPropagation();

    if (!confirm("Are you sure you want to delete this chat?")) {
      return;
    }

    try {
      const success = await deleteSessionByThreadId(session.sessionId);
      if (success) {
        toast.success("Chat deleted successfully");
        if (session.sessionId === currentThreadId) {
          onSessionDeleted(session.sessionId);
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
      await clearAllSessions();
      toast.success("All chats cleared");
      onAllSessionsCleared();
    } catch (error) {
      console.error("Clear all error:", error);
      toast.error("Error clearing chats");
    }
  };

  const startEditing = (session: ChatSessionRecordSummary, e: React.MouseEvent) => {
    e.stopPropagation();
    setEditingSessionId(session.sessionId);
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
            const isActive = session.sessionId === currentThreadId;
            const isEditing = editingSessionId === session.sessionId;

            return (
              <div
                key={session.sessionId}
                onClick={() => !isEditing && onSessionSelect(session.sessionId)}
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
                              saveEdit(session.sessionId, e as any);
                            } else if (e.key === "Escape") {
                              cancelEditing(e as any);
                            }
                          }}
                        />
                        <Button
                          size="sm"
                          variant="ghost"
                          className="h-6 w-6 p-0"
                          onClick={(e) => saveEdit(session.sessionId, e)}
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
                          <span>{formatDate(session.createTime)}</span>
                          <span>•</span>
                          <span>{session.messageCount ?? 0} msgs</span>
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
                  {sessions.length}
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
              <p className="mb-2">Chat history is stored on the server.</p>
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
