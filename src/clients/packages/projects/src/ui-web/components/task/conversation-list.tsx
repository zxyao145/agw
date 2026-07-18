"use client";

import * as React from "react";
import { Info, Pencil, Plus, RotateCw, Trash2 } from "lucide-react";
import { toast } from "sonner";

import {
  deleteAllProjectContexts,
  deleteProjectContext,
  getProjectContexts,
  updateProjectContextTitle,
  type ContextSummary,
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
import { cn } from "@agw/components";

interface ConversationListProps {
  projectId: string;
  currentContextId: string | null;
  refreshSignal?: number;
  onContextSelect: (context: ContextSummary) => void;
  onActiveContextResolved?: (context: ContextSummary) => void;
  onNewConversation: () => void;
  onAllConversationsDeleted: () => void;
  headerActions?: React.ReactNode;
}

export function ConversationList({
  projectId,
  currentContextId,
  refreshSignal,
  onContextSelect,
  onActiveContextResolved,
  onNewConversation,
  onAllConversationsDeleted,
  headerActions,
}: ConversationListProps) {
  const [contexts, setContexts] = React.useState<ContextSummary[]>([]);
  const [infoModalOpen, setInfoModalOpen] = React.useState(false);
  const [clearAllDialogOpen, setClearAllDialogOpen] = React.useState(false);
  const [contextToDelete, setContextToDelete] = React.useState<ContextSummary | null>(null);
  const [contextToRename, setContextToRename] = React.useState<ContextSummary | null>(null);
  const [renameTitle, setRenameTitle] = React.useState("");
  const [isRefreshing, setIsRefreshing] = React.useState(false);
  const didObserveRefreshSignalRef = React.useRef(false);
  const refreshRequestIdRef = React.useRef(0);

  const matchesCurrentSession = React.useCallback(
    (context: ContextSummary) => context.contextId === currentContextId,
    [currentContextId],
  );

  const refreshContexts = React.useCallback(async (): Promise<ContextSummary[]> => {
    const requestId = ++refreshRequestIdRef.current;
    setIsRefreshing(true);
    try {
      if (!projectId) {
        if (requestId === refreshRequestIdRef.current) {
          setContexts([]);
        }
        return [];
      }

      const latestContexts = await getProjectContexts(projectId);
      if (requestId !== refreshRequestIdRef.current) {
        return latestContexts;
      }

      setContexts(latestContexts);
      return latestContexts;
    } catch (error) {
      console.error("Failed to load chat contexts:", error);
      return [];
    } finally {
      if (requestId === refreshRequestIdRef.current) {
        setIsRefreshing(false);
      }
    }
  }, [projectId]);

  React.useEffect(() => {
    void refreshContexts();
  }, [refreshContexts]);

  React.useEffect(() => {
    if (refreshSignal === undefined) {
      return;
    }

    if (!didObserveRefreshSignalRef.current) {
      didObserveRefreshSignalRef.current = true;
      return;
    }

    void refreshContexts();
  }, [refreshSignal, refreshContexts]);

  React.useEffect(() => {
    if (!projectId || !currentContextId) {
      return;
    }

    let cancelled = false;
    let timeoutId: ReturnType<typeof setTimeout> | null = null;
    let attempts = 0;

    const refreshUntilCurrentContextAppears = async () => {
      attempts += 1;
      const latestContexts = await refreshContexts();
      if (cancelled || attempts >= 5 || latestContexts.some(matchesCurrentSession)) {
        return;
      }

      timeoutId = setTimeout(() => {
        void refreshUntilCurrentContextAppears();
      }, 500);
    };

    void refreshUntilCurrentContextAppears();

    return () => {
      cancelled = true;
      if (timeoutId) {
        clearTimeout(timeoutId);
      }
    };
  }, [currentContextId, matchesCurrentSession, projectId, refreshContexts]);

  const activeContext = React.useMemo(() => {
    if (!currentContextId) {
      return null;
    }

    return contexts.find(matchesCurrentSession) ?? null;
  }, [contexts, currentContextId, matchesCurrentSession]);

  React.useEffect(() => {
    if (
      !activeContext ||
      !onActiveContextResolved ||
      activeContext.contextId === currentContextId
    ) {
      return;
    }

    onActiveContextResolved(activeContext);
  }, [activeContext, currentContextId, onActiveContextResolved]);

  const handleClearAll = async () => {
    try {
      await deleteAllProjectContexts(projectId);
      toast.success("All chats cleared");
      onAllConversationsDeleted();
      await refreshContexts();
    } catch (error) {
      console.error("Clear all error:", error);
      toast.error("Error clearing chats");
    }
  };

  const handleDeleteContext = async (context: ContextSummary) => {
    try {
      const deleted = await deleteProjectContext(projectId, context.contextId);
      if (!deleted) {
        toast.error("Conversation not found");
        await refreshContexts();
        return;
      }

      toast.success("Conversation deleted");
      if (context.contextId === currentContextId) {
        onAllConversationsDeleted();
      }
      await refreshContexts();
    } catch (error) {
      console.error("Delete conversation error:", error);
      toast.error("Error deleting conversation");
    }
  };

  const handleRenameContext = async () => {
    if (!contextToRename) {
      return;
    }

    try {
      const updated = await updateProjectContextTitle(
        projectId,
        contextToRename.contextId,
        renameTitle,
      );
      if (!updated) {
        toast.error(renameTitle.trim() ? "Conversation not found" : "Title is required");
        await refreshContexts();
        return;
      }

      toast.success("Conversation renamed");
      setContextToRename(null);
      setRenameTitle("");
      await refreshContexts();
    } catch (error) {
      console.error("Rename conversation error:", error);
      toast.error("Error renaming conversation");
    }
  };

  return (
    <div className="flex flex-col bg-muted/30 w-full h-full min-h-0">
      <div className="p-4 border-b flex items-center justify-between">
        <h2 className="font-semibold text-sm">Chat Contexts</h2>
        <div className="tools">
          <Button
            className="cursor-pointer"
            size="sm"
            variant="ghost"
            onClick={refreshContexts}
            disabled={isRefreshing}
            aria-label="Refresh chat contexts"
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
              await refreshContexts();
            }}
          >
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

      <div className="flex-1 overflow-y-auto agw-scrollbar p-2 space-y-1">
        {contexts.length === 0 ? (
          <div className="text-center py-8 text-muted-foreground text-sm">No chat history yet</div>
        ) : (
          contexts.map((context) => {
            const isActive = context.contextId === activeContext?.contextId;

            return (
              <div
                key={context.contextId}
                onClick={() => onContextSelect(context)}
                className={cn(
                  "group p-2 rounded-md cursor-pointer transition-colors",
                  isActive ? "bg-accent" : "bg-card hover:bg-accent/50",
                )}
              >
                <div className="flex items-start">
                  <div className="flex-1 min-w-0 space-y-1">
                    <div className="font-medium text-sm truncate">
                      {context.title || "Untitled"}
                    </div>
                    <div className="mt-2 text-xs text-muted-foreground flex gap-1.5">
                      <span>
                        {/* {context.executionCount}{" "}
                        {context.executionCount === 1
                          ? "execution"
                          : "executions"}{" "}
                        ·  */}
                        {context.messageCount} {context.messageCount === 1 ? "message" : "messages"}
                      </span>
                      ·
                      <span>
                        {formatFriendlyLocalDateTime(context.updateTime ?? context.createTime)}
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
                        setContextToRename(context);
                        setRenameTitle(context.title || "");
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
                        setContextToDelete(context);
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

      <Dialog open={infoModalOpen} onOpenChange={setInfoModalOpen}>
        <DialogContent size="sm">
          <DialogHeader>
            <DialogTitle>Chat History Storage</DialogTitle>
            <DialogDescription>Storage statistics and management options</DialogDescription>
          </DialogHeader>
          <div className="space-y-4 py-4">
            <div className="space-y-2">
              <div className="flex justify-between text-sm">
                <span className="text-muted-foreground">Total contexts:</span>
                <span className="font-mono font-medium">{contexts.length}</span>
              </div>
            </div>

            {contexts.length > 0 && (
              <Button
                variant="destructive"
                className="w-full"
                onClick={() => {
                  setInfoModalOpen(false);
                  setClearAllDialogOpen(true);
                }}
              >
                <Trash2 className="h-4 w-4" />
                Delete All History
              </Button>
            )}

            <div className="text-xs text-muted-foreground border-t pt-4">
              <p className="mb-2">Chat history is stored on the server.</p>
            </div>
          </div>
        </DialogContent>
      </Dialog>

      <Dialog
        open={Boolean(contextToRename)}
        onOpenChange={(open) => {
          if (!open) {
            setContextToRename(null);
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
                  void handleRenameContext();
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
                setContextToRename(null);
                setRenameTitle("");
              }}
            >
              Cancel
            </Button>
            <Button onClick={() => void handleRenameContext()} disabled={!renameTitle.trim()}>
              Save
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog
        open={Boolean(contextToDelete)}
        onOpenChange={(open) => !open && setContextToDelete(null)}
      >
        <DialogContent size="sm">
          <DialogHeader>
            <DialogTitle>Delete conversation</DialogTitle>
            <DialogDescription className="whitespace-normal wrap-break-word break-all">
              This will permanently delete "{contextToDelete?.title || "Untitled"}" and all
              executions in this conversation.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setContextToDelete(null)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={async () => {
                if (!contextToDelete) {
                  return;
                }

                const context = contextToDelete;
                setContextToDelete(null);
                await handleDeleteContext(context);
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
