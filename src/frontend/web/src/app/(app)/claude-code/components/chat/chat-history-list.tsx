"use client";

import * as React from "react";
import { MessageSquare, Trash2, Plus, Edit2, Check, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  subscribeToSessions,
  deleteSession,
  updateSessionTitle,
  clearAllSessions,
} from "../../lib/chat-history-service";
import type { ChatSessionDocument } from "../../lib/chat-history-db";
import { cn } from "@/lib/utils";

interface ChatHistoryListProps {
  currentThreadId: string | null;
  onSessionSelect: (session: ChatSessionDocument) => void;
  onNewChat: () => void;
}

export function ChatHistoryList({
  currentThreadId,
  onSessionSelect,
  onNewChat,
}: ChatHistoryListProps) {
  const [sessions, setSessions] = React.useState<ChatSessionDocument[]>([]);
  const [editingSessionId, setEditingSessionId] = React.useState<string | null>(null);
  const [editTitle, setEditTitle] = React.useState("");

  // Subscribe to session changes
  React.useEffect(() => {
    const unsubscribe = subscribeToSessions((newSessions) => {
      setSessions(newSessions);
    });

    return unsubscribe;
  }, []);

  const handleDelete = async (sessionId: string, e: React.MouseEvent) => {
    e.stopPropagation();

    if (!confirm("Are you sure you want to delete this chat?")) {
      return;
    }

    try {
      const success = await deleteSession(sessionId);
      if (success) {
        toast.success("Chat deleted successfully");
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
    } catch (error) {
      console.error("Clear all error:", error);
      toast.error("Error clearing chats");
    }
  };

  const startEditing = (session: ChatSessionDocument, e: React.MouseEvent) => {
    e.stopPropagation();
    setEditingSessionId(session.id);
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
    const diffDays = Math.floor(diffMs / 86400000);

    if (diffMins < 1) return "Just now";
    if (diffMins < 60) return `${diffMins}m ago`;
    if (diffHours < 24) return `${diffHours}h ago`;
    if (diffDays < 7) return `${diffDays}d ago`;

    return date.toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
    });
  };

  const formatSize = (bytes: number) => {
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
        <Button size="sm" variant="ghost" onClick={onNewChat}>
          <Plus className="h-4 w-4" />
        </Button>
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
            const isEditing = editingSessionId === session.id;

            return (
              <div
                key={session.id}
                onClick={() => !isEditing && onSessionSelect(session)}
                className={cn(
                  "group p-3 rounded-md cursor-pointer transition-colors border",
                  isActive
                    ? "bg-primary/10 border-primary/20"
                    : "bg-card hover:bg-accent/50 border-transparent"
                )}
              >
                <div className="flex items-start gap-2">
                  <MessageSquare className="h-4 w-4 mt-0.5 flex-shrink-0 text-muted-foreground" />
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
                              saveEdit(session.id, e as any);
                            } else if (e.key === "Escape") {
                              cancelEditing(e as any);
                            }
                          }}
                        />
                        <Button
                          size="sm"
                          variant="ghost"
                          className="h-6 w-6 p-0"
                          onClick={(e) => saveEdit(session.id, e)}
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
                          <span>{formatDate(session.updatedAt)}</span>
                          <span>•</span>
                          <span>{session.messages.length} msgs</span>
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
                        onClick={(e) => handleDelete(session.id, e)}
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

      {/* Footer with stats */}
      <div className="p-3 border-t text-xs text-muted-foreground space-y-1">
        <div className="flex justify-between">
          <span>Total sessions:</span>
          <span className="font-mono">{sessions.length} / 1000</span>
        </div>
        <div className="flex justify-between">
          <span>Storage used:</span>
          <span className="font-mono">{formatSize(totalSize)} / 200 MB</span>
        </div>
        {sessions.length > 0 && (
          <Button
            size="sm"
            variant="outline"
            className="w-full mt-2"
            onClick={handleClearAll}
          >
            Clear All History
          </Button>
        )}
      </div>
    </div>
  );
}
