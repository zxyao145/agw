"use client";

import * as React from "react";
import { Pencil, Plus, RotateCw, Trash2 } from "lucide-react";
import { toast } from "sonner";
import {
  useInfiniteQuery,
  useQuery,
  useQueryClient,
  type InfiniteData,
} from "@agw/components/query";
import { ApiError } from "@agw/api";

import {
  deleteAllProjectConversations,
  deleteProjectConversation,
  getProjectConversationDetails,
  getProjectConversations,
  updateProjectConversationTitle,
  type ConversationPage,
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

const CONVERSATION_PAGE_SIZE = 20;
const CONVERSATION_STALE_TIME_MS = 30_000;
const CONVERSATION_GC_TIME_MS = 30 * 60_000;

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
  const queryClient = useQueryClient();
  const [clearAllDialogOpen, setClearAllDialogOpen] = React.useState(false);
  const [conversationToDelete, setConversationToDelete] =
    React.useState<ConversationSummary | null>(null);
  const [conversationToRename, setConversationToRename] =
    React.useState<ConversationSummary | null>(null);
  const [renameTitle, setRenameTitle] = React.useState("");
  const observedRefreshSignalRef = React.useRef(refreshSignal);
  const refreshStateRef = React.useRef<{
    projectId: string;
    promise: Promise<void>;
    refreshAgain: boolean;
  } | null>(null);
  const resolvedConversationIdRef = React.useRef<string | null>(null);
  const listScrollRef = React.useRef<HTMLDivElement | null>(null);
  const loadMoreRef = React.useRef<HTMLDivElement | null>(null);
  const queryKey = React.useMemo(() => ["project-conversations", projectId] as const, [projectId]);

  const conversationsQuery = useInfiniteQuery({
    queryKey,
    enabled: Boolean(projectId),
    initialPageParam: 1,
    queryFn: ({ pageParam, signal }) =>
      getProjectConversations(projectId, {
        pageIndex: pageParam,
        pageSize: CONVERSATION_PAGE_SIZE,
        signal,
      }),
    getNextPageParam: (lastPage) =>
      lastPage.pageIndex * lastPage.pageSize < lastPage.total ? lastPage.pageIndex + 1 : undefined,
    staleTime: CONVERSATION_STALE_TIME_MS,
    gcTime: CONVERSATION_GC_TIME_MS,
  });

  const conversations = React.useMemo(() => {
    const conversationsById = new Map<string, ConversationSummary>();
    for (const page of conversationsQuery.data?.pages ?? []) {
      for (const conversation of page.items) {
        if (!conversationsById.has(conversation.conversationId)) {
          conversationsById.set(conversation.conversationId, conversation);
        }
      }
    }
    return [...conversationsById.values()];
  }, [conversationsQuery.data]);

  const matchesCurrentSession = React.useCallback(
    (conversation: ConversationSummary) => conversation.conversationId === currentConversationId,
    [currentConversationId],
  );

  const refreshConversations = React.useCallback((): Promise<void> => {
    if (!projectId) return Promise.resolve();
    const activeRefresh = refreshStateRef.current;
    if (activeRefresh?.projectId === projectId) {
      activeRefresh.refreshAgain = true;
      return activeRefresh.promise;
    }

    const refreshState = {
      projectId,
      promise: Promise.resolve(),
      refreshAgain: false,
    };
    const refresh = async () => {
      do {
        refreshState.refreshAgain = false;
        const summaryQueryKey = ["project-conversation-summary", projectId] as const;
        await Promise.all([
          queryClient.cancelQueries({ queryKey, exact: true }),
          queryClient.cancelQueries({ queryKey: summaryQueryKey }),
        ]);
        if (!queryClient.getQueryCache().find({ queryKey, exact: true })?.isActive()) {
          await Promise.all([
            queryClient.invalidateQueries({ queryKey, exact: true, refetchType: "none" }),
            queryClient.invalidateQueries({ queryKey: summaryQueryKey, refetchType: "none" }),
          ]);
          return;
        }
        queryClient.setQueryData<InfiniteData<ConversationPage>>(queryKey, (current) =>
          current
            ? {
                pages: current.pages.slice(0, 1),
                pageParams: current.pageParams.slice(0, 1),
              }
            : current,
        );
        await Promise.all([
          queryClient.refetchQueries({ queryKey, exact: true, type: "active" }),
          queryClient.invalidateQueries({ queryKey: summaryQueryKey, refetchType: "active" }),
        ]);
        if (!queryClient.getQueryCache().find({ queryKey, exact: true })?.isActive()) {
          await queryClient.invalidateQueries({ queryKey, exact: true, refetchType: "none" });
          return;
        }
      } while (refreshState.refreshAgain);
    };
    refreshState.promise = refresh().finally(() => {
      if (refreshStateRef.current === refreshState) refreshStateRef.current = null;
    });
    refreshStateRef.current = refreshState;
    return refreshState.promise;
  }, [projectId, queryClient, queryKey]);

  React.useEffect(() => {
    if (observedRefreshSignalRef.current === refreshSignal) return;
    observedRefreshSignalRef.current = refreshSignal;
    void refreshConversations();
  }, [refreshConversations, refreshSignal]);

  const shouldResolveCurrentConversation = Boolean(
    projectId &&
    currentConversationId &&
    conversationsQuery.isSuccess &&
    !conversations.some(matchesCurrentSession),
  );
  const currentConversationQuery = useQuery({
    queryKey: ["project-conversation-summary", projectId, currentConversationId],
    enabled: shouldResolveCurrentConversation,
    queryFn: async ({ signal }) => {
      try {
        return await getProjectConversationDetails(
          projectId,
          currentConversationId!,
          undefined,
          signal,
        );
      } catch (error) {
        if (error instanceof ApiError && error.status === 404) return null;
        throw error;
      }
    },
    retry: false,
    staleTime: CONVERSATION_STALE_TIME_MS,
    gcTime: CONVERSATION_GC_TIME_MS,
  });
  const displayedConversations = React.useMemo(() => {
    const current = currentConversationQuery.data;
    return current && !conversations.some((item) => item.conversationId === current.conversationId)
      ? [current, ...conversations]
      : conversations;
  }, [conversations, currentConversationQuery.data]);

  const activeConversation = React.useMemo(() => {
    if (!currentConversationId) {
      return null;
    }

    return displayedConversations.find(matchesCurrentSession) ?? null;
  }, [currentConversationId, displayedConversations, matchesCurrentSession]);

  React.useEffect(() => {
    const root = listScrollRef.current;
    const target = loadMoreRef.current;
    if (!root || !target || !conversationsQuery.hasNextPage) return;

    const observer = new IntersectionObserver(
      (entries) => {
        if (
          entries.some((entry) => entry.isIntersecting) &&
          !conversationsQuery.isFetchingNextPage
        ) {
          void conversationsQuery.fetchNextPage();
        }
      },
      { root, rootMargin: "160px" },
    );
    observer.observe(target);
    return () => observer.disconnect();
  }, [
    conversationsQuery.fetchNextPage,
    conversationsQuery.hasNextPage,
    conversationsQuery.isFetchingNextPage,
  ]);

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

  const isRefreshing = conversationsQuery.isFetching && !conversationsQuery.isFetchingNextPage;

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
            }}
          >
            <Plus className="h-4 w-4" />
          </Button>
          {displayedConversations.length > 0 && (
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

      <div ref={listScrollRef} className="flex-1 overflow-y-auto agw-scrollbar p-2 space-y-1">
        {conversationsQuery.isPending ? (
          <div className="space-y-2 p-1" aria-label="Loading conversations">
            {Array.from({ length: 5 }, (_, index) => (
              <div key={index} className="h-14 animate-pulse rounded-md bg-muted" />
            ))}
          </div>
        ) : conversationsQuery.isError && displayedConversations.length === 0 ? (
          <div className="space-y-3 py-8 text-center text-sm text-muted-foreground">
            <div>Failed to load conversations</div>
            <Button size="sm" variant="outline" onClick={() => void refreshConversations()}>
              Retry
            </Button>
          </div>
        ) : displayedConversations.length === 0 ? (
          <div className="text-center py-8 text-muted-foreground text-sm">No chat history yet</div>
        ) : (
          displayedConversations.map((conversation) => {
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
        <div ref={loadMoreRef} className="h-1" aria-hidden="true" />
        {conversationsQuery.isFetchingNextPage ? (
          <div className="py-3 text-center text-xs text-muted-foreground">
            Loading more conversations...
          </div>
        ) : conversationsQuery.isFetchNextPageError ? (
          <div className="py-3 text-center">
            <Button
              size="sm"
              variant="ghost"
              onClick={() => void conversationsQuery.fetchNextPage()}
            >
              Retry loading more
            </Button>
          </div>
        ) : null}
        {conversationsQuery.isError &&
        displayedConversations.length > 0 &&
        !conversationsQuery.isFetchNextPageError ? (
          <div className="space-y-2 py-3 text-center text-xs text-destructive">
            <div>Failed to refresh conversations</div>
            <Button size="sm" variant="ghost" onClick={() => void refreshConversations()}>
              Retry refresh
            </Button>
          </div>
        ) : null}
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
