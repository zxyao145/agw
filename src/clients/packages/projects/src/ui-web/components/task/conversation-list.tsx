"use client";

import * as React from "react";
import { Pencil, Plus, RotateCw, Trash2 } from "lucide-react";
import { toast } from "sonner";

import {
  deleteAllProjectConversations,
  deleteProjectConversation,
  getProjectConversations,
  updateProjectConversationTitle,
  type ConversationSummary,
} from "../../../services/task-client";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";
import { Input } from "@agw/components";
import { formatFriendlyLocalDateTime } from "@agw/components";
import { Tooltip, TooltipContent, TooltipTrigger } from "@agw/components";
import { cn } from "@agw/components";

interface ConversationListProps {
  projectId: string;
  currentConversationId: string | null;
  refreshSignal?: number;
  onConversationSelect: (conversation: ConversationSummary) => void;
  onActiveConversationResolved?: (conversation: ConversationSummary) => void;
  onNewConversation: () => void;
  onAllConversationsDeleted: () => void;
  headerActions?: React.ReactNode;
}

export function ConversationList({
  projectId,
  currentConversationId,
  refreshSignal,
  onConversationSelect,
  onActiveConversationResolved,
  onNewConversation,
  onAllConversationsDeleted,
  headerActions,
}: ConversationListProps) {
  const [conversations, setConversations] = React.useState<ConversationSummary[]>([]);
  const [clearAllDialogOpen, setClearAllDialogOpen] = React.useState(false);
  const [conversationToDelete, setConversationToDelete] =
    React.useState<ConversationSummary | null>(null);
  const [conversationToRename, setConversationToRename] =
    React.useState<ConversationSummary | null>(null);
  const [renameTitle, setRenameTitle] = React.useState("");
  const [isRefreshing, setIsRefreshing] = React.useState(false);
  const didObserveRefreshSignalRef = React.useRef(false);
  const refreshRequestIdRef = React.useRef(0);
  const resolvedConversationIdRef = React.useRef<string | null>(null);

  const matchesCurrentSession = React.useCallback(
    (conversation: ConversationSummary) => conversation.conversationId === currentConversationId,
    [currentConversationId],
  );

  const refreshConversations = React.useCallback(async (): Promise<ConversationSummary[]> => {
    const requestId = ++refreshRequestIdRef.current;
    setIsRefreshing(true);
    try {
      if (!projectId) {
        if (requestId === refreshRequestIdRef.current) {
          setConversations([]);
        }
        return [];
      }

      const latestConversations = await getProjectConversations(projectId);
      if (requestId !== refreshRequestIdRef.current) {
        return latestConversations;
      }

      setConversations(latestConversations);
      return latestConversations;
    } catch (error) {
      console.error("Failed to load conversations:", error);
      return [];
    } finally {
      if (requestId === refreshRequestIdRef.current) {
        setIsRefreshing(false);
      }
    }
  }, [projectId]);

  React.useEffect(() => {
    void refreshConversations();
  }, [refreshConversations]);

  React.useEffect(() => {
    if (refreshSignal === undefined) {
      return;
    }

    if (!didObserveRefreshSignalRef.current) {
      didObserveRefreshSignalRef.current = true;
      return;
    }

    void refreshConversations();
  }, [refreshSignal, refreshConversations]);

  React.useEffect(() => {
    if (!projectId || !currentConversationId) {
      return;
    }

    let cancelled = false;
    let timeoutId: ReturnType<typeof setTimeout> | null = null;
    let attempts = 0;

    const refreshUntilCurrentConversationAppears = async () => {
      attempts += 1;
      const latestConversations = await refreshConversations();
      if (cancelled || attempts >= 5 || latestConversations.some(matchesCurrentSession)) {
        return;
      }

      timeoutId = setTimeout(() => {
        void refreshUntilCurrentConversationAppears();
      }, 500);
    };

    void refreshUntilCurrentConversationAppears();

    return () => {
      cancelled = true;
      if (timeoutId) {
        clearTimeout(timeoutId);
      }
    };
  }, [currentConversationId, matchesCurrentSession, projectId, refreshConversations]);

  const activeConversation = React.useMemo(() => {
    if (!currentConversationId) {
      return null;
    }

    return conversations.find(matchesCurrentSession) ?? null;
  }, [conversations, currentConversationId, matchesCurrentSession]);

  React.useEffect(() => {
    if (
      !activeConversation ||
      !onActiveConversationResolved ||
      resolvedConversationIdRef.current === activeConversation.conversationId
    ) {
      return;
    }

    resolvedConversationIdRef.current = activeConversation.conversationId;
    onActiveConversationResolved(activeConversation);
  }, [activeConversation, onActiveConversationResolved]);

  const handleClearAll = async () => {
    try {
      await deleteAllProjectConversations(projectId);
      toast.success("All chats cleared");
      onAllConversationsDeleted();
      await refreshConversations();
    } catch (error) {
      console.error("Clear all error:", error);
      toast.error("Error clearing chats");
    }
  };

  const handleDeleteConversation = async (conversation: ConversationSummary) => {
    try {
      const deleted = await deleteProjectConversation(projectId, conversation.conversationId);
      if (!deleted) {
        toast.error("Conversation not found");
        await refreshConversations();
        return;
      }

      toast.success("Conversation deleted");
      if (conversation.conversationId === currentConversationId) {
        onAllConversationsDeleted();
      }
      await refreshConversations();
    } catch (error) {
      console.error("Delete conversation error:", error);
      toast.error("Error deleting conversation");
    }
  };

  const handleRenameConversation = async () => {
    if (!conversationToRename) {
      return;
    }

    try {
      const updated = await updateProjectConversationTitle(
        projectId,
        conversationToRename.conversationId,
        renameTitle,
      );
      if (!updated) {
        toast.error(renameTitle.trim() ? "Conversation not found" : "Title is required");
        await refreshConversations();
        return;
      }

      toast.success("Conversation renamed");
      setConversationToRename(null);
      setRenameTitle("");
      await refreshConversations();
    } catch (error) {
      console.error("Rename conversation error:", error);
      toast.error("Error renaming conversation");
    }
  };

  return (
    <div className="flex flex-col bg-muted/30 w-full h-full min-h-0">
      <div className="p-4 border-b flex items-center justify-between">
        <h2 className="font-semibold text-sm">Conversations</h2>
        <div className="tools">
          <Button
            className="cursor-pointer"
            size="sm"
            variant="ghost"
            onClick={refreshConversations}
            disabled={isRefreshing}
            aria-label="Refresh conversations"
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
              await Promise.resolve(onNewConversation());
              await refreshConversations();
            }}
          >
            <Plus className="h-4 w-4" />
          </Button>
          {conversations.length > 0 && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  className="cursor-pointer hover:text-destructive"
                  size="sm"
                  variant="ghost"
                  aria-label="Delete All History"
                  onClick={() => setClearAllDialogOpen(true)}
                >
                  <Trash2 className="h-4 w-4" />
                </Button>
              </TooltipTrigger>
              <TooltipContent>Delete All History</TooltipContent>
            </Tooltip>
          )}
          {headerActions}
        </div>
      </div>

      <div className="flex-1 overflow-y-auto agw-scrollbar p-2 space-y-1">
        {conversations.length === 0 ? (
          <div className="text-center py-8 text-muted-foreground text-sm">No chat history yet</div>
        ) : (
          conversations.map((conversation) => {
            const isActive = conversation.conversationId === activeConversation?.conversationId;

            return (
              <div
                key={conversation.conversationId}
                onClick={() => onConversationSelect(conversation)}
                className={cn(
                  "group p-2 rounded-md cursor-pointer transition-colors",
                  isActive ? "bg-accent" : "bg-card hover:bg-accent/50",
                )}
              >
                <div className="flex items-start">
                  <div className="flex-1 min-w-0 space-y-1">
                    <div className="font-medium text-sm truncate">
                      {conversation.title || "Untitled"}
                    </div>
                    <div className="mt-2 text-xs text-muted-foreground flex gap-1.5">
                      <span>
                        {/* {context.executionCount}{" "}
                        {context.executionCount === 1
                          ? "execution"
                          : "executions"}{" "}
                        ·  */}
                        {conversation.messageCount}{" "}
                        {conversation.messageCount === 1 ? "message" : "messages"}
                      </span>
                      ·
                      <span>
                        {formatFriendlyLocalDateTime(
                          conversation.updateTime ?? conversation.createTime,
                        )}
                      </span>
                    </div>
                  </div>
                  <div className="hidden group-hover:flex opacity-0 group-hover:opacity-100">
                    <Button
                      size="icon"
                      variant="ghost"
                      className="h-7 w-7 text-muted-foreground"
                      aria-label="Rename conversation"
                      onClick={(event) => {
                        event.stopPropagation();
                        setConversationToRename(conversation);
                        setRenameTitle(conversation.title || "");
                      }}
                    >
                      <Pencil className="h-4 w-4" />
                    </Button>
                    <Button
                      size="icon"
                      variant="ghost"
                      className="h-7 w-7 text-muted-foreground hover:text-destructive"
                      aria-label="Delete conversation"
                      onClick={(event) => {
                        event.stopPropagation();
                        setConversationToDelete(conversation);
                      }}
                    >
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  </div>
                </div>
              </div>
            );
          })
        )}
      </div>

      <Dialog
        open={Boolean(conversationToRename)}
        onOpenChange={(open) => {
          if (!open) {
            setConversationToRename(null);
            setRenameTitle("");
          }
        }}
      >
        <DialogContent size="sm">
          <DialogHeader>
            <DialogTitle>Rename conversation</DialogTitle>
            <DialogDescription>Update the title shown for this conversation.</DialogDescription>
          </DialogHeader>
          <div className="py-2">
            <Input
              value={renameTitle}
              onChange={(event) => setRenameTitle(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === "Enter") {
                  void handleRenameConversation();
                }
              }}
              placeholder="Conversation title"
              autoFocus
            />
          </div>
          <DialogFooter>
            <Button
              variant="outline"
              onClick={() => {
                setConversationToRename(null);
                setRenameTitle("");
              }}
            >
              Cancel
            </Button>
            <Button onClick={() => void handleRenameConversation()} disabled={!renameTitle.trim()}>
              Save
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog
        open={Boolean(conversationToDelete)}
        onOpenChange={(open) => !open && setConversationToDelete(null)}
      >
        <DialogContent size="sm">
          <DialogHeader>
            <DialogTitle>Delete conversation</DialogTitle>
            <DialogDescription className="whitespace-normal wrap-break-word break-all">
              This will permanently delete "{conversationToDelete?.title || "Untitled"}" and all
              executions in this conversation.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConversationToDelete(null)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={async () => {
                if (!conversationToDelete) {
                  return;
                }

                const conversation = conversationToDelete;
                setConversationToDelete(null);
                await handleDeleteConversation(conversation);
              }}
            >
              Delete
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={clearAllDialogOpen} onOpenChange={setClearAllDialogOpen}>
        <DialogContent size="sm">
          <DialogHeader>
            <DialogTitle>Clear all chat history</DialogTitle>
            <DialogDescription>
              This will permanently delete all conversations and executions for this project.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setClearAllDialogOpen(false)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={async () => {
                setClearAllDialogOpen(false);
                await handleClearAll();
              }}
            >
              Clear all
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
