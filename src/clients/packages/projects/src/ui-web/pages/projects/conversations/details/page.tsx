"use client";

import * as React from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { LoaderCircle, RotateCw } from "lucide-react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useInfiniteQuery, useQuery } from "@agw/components/query";

import {
  getProjectConversationDetails,
  getProjectConversationMessages,
  type ConversationDetails,
} from "../../../../../services/task-client";
import type { AiMessage } from "@agw/api";
import { getApiErrorMessage } from "@agw/api";
import { Button, ButtonGroup } from "@agw/components";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@agw/components";
import { formatLocalDateTime } from "@agw/components";

function statusLabel(status: number): string {
  switch (status) {
    case 0:
      return "Pending";
    case 1:
      return "Running";
    case 2:
      return "Succeeded";
    case 3:
      return "Failed";
    case 4:
      return "Canceled";
    default:
      return `#${status}`;
  }
}

function statusClassName(status: number): string {
  switch (status) {
    case 2:
      return "bg-emerald-500/10 text-emerald-700 dark:text-emerald-300";
    case 1:
      return "bg-blue-500/10 text-blue-700 dark:text-blue-300";
    case 0:
    case 4:
      return "bg-muted text-muted-foreground";
    case 3:
      return "bg-destructive/10 text-destructive";
    default:
      return "bg-muted text-muted-foreground";
  }
}

function getChatHref(projectId: string, conversation: ConversationDetails): string {
  const searchParams = new URLSearchParams({
    projectId,
    conversationId: conversation.conversationId,
  });

  return `/chat?${searchParams.toString()}`;
}

function formatMessageContent(content: unknown): string {
  if (typeof content === "string") {
    return content || "-";
  }

  if (content == null) {
    return "-";
  }

  return JSON.stringify(content);
}

export default function ConversationDetailsPage() {
  const searchParams = useSearchParams();
  const projectId = searchParams.get("projectId") ?? "";
  const conversationId = searchParams.get("conversationId") ?? "";
  const enabled = Boolean(projectId && conversationId);

  const conversationQuery = useQuery({
    queryKey: ["projects", projectId, "conversations", conversationId],
    enabled,
    queryFn: ({ signal }) =>
      getProjectConversationDetails(projectId, conversationId, undefined, signal),
  });
  const messagesQuery = useInfiniteQuery({
    queryKey: ["projects", projectId, "conversations", conversationId, "messages"],
    enabled,
    initialPageParam: null as string | null,
    queryFn: ({ pageParam, signal }) =>
      getProjectConversationMessages(projectId, conversationId, {
        direction: "newer",
        cursor: pageParam,
        pageSize: 50,
        signal,
      }),
    getNextPageParam: (lastPage) =>
      lastPage.hasMore && lastPage.nextCursor ? lastPage.nextCursor : undefined,
  });

  const conversation = conversationQuery.data;
  const messages = React.useMemo(
    () => messagesQuery.data?.pages.flatMap((page) => page.items) ?? [],
    [messagesQuery.data],
  );
  const loadMoreMessages = React.useCallback(() => {
    if (messages.length === 0 && messagesQuery.isError) {
      void messagesQuery.refetch();
      return;
    }
    void messagesQuery.fetchNextPage();
  }, [messages.length, messagesQuery.fetchNextPage, messagesQuery.isError, messagesQuery.refetch]);

  return (
    <div className="w-full min-w-0 max-w-full space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div className="min-w-0 space-y-1">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="truncate text-xl font-semibold">
              {conversationQuery.isLoading
                ? "Loading conversation..."
                : (conversation?.title ?? "Conversation")}
            </h1>
            {conversation?.latestStatus !== null && conversation?.latestStatus !== undefined ? (
              <span
                className={`rounded-md px-2 py-0.5 text-xs ${statusClassName(conversation.latestStatus)}`}
              >
                {statusLabel(conversation.latestStatus)}
              </span>
            ) : null}
          </div>
          <div className="text-sm text-muted-foreground">
            Conversation history across related executions.
          </div>
          {conversation ? (
            <div className="text-xs text-muted-foreground">
              <span className="font-mono">{conversation.conversationId}</span>
            </div>
          ) : null}
        </div>

        <ButtonGroup>
          {conversation ? (
            <Button asChild size="sm">
              <Link href={getChatHref(projectId, conversation)}>Continue In Chat</Link>
            </Button>
          ) : null}
          <Button asChild variant="outline" size="sm">
            <Link href={`/projects/details/?projectId=${encodeURIComponent(projectId)}`}>Back</Link>
          </Button>
        </ButtonGroup>
      </div>

      {!enabled ? (
        <div className="text-sm text-destructive">Project ID and conversation ID are required.</div>
      ) : conversationQuery.isLoading ? (
        <div className="text-sm text-muted-foreground">Loading conversation details...</div>
      ) : conversationQuery.isError ? (
        <div className="text-sm text-destructive">
          Failed to load conversation: {getApiErrorMessage(conversationQuery.error)}
        </div>
      ) : conversation ? (
        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>Conversation Overview</CardTitle>
              <CardDescription>Aggregate metadata for this conversation.</CardDescription>
            </CardHeader>
            <CardContent className="grid gap-4 text-sm sm:grid-cols-2">
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">
                  Conversation ID
                </div>
                <div className="break-all font-mono text-xs">{conversation.conversationId}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">
                  Execution Context ID
                </div>
                <div className="break-all font-mono text-xs">{conversation.contextId}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">
                  Execution Count
                </div>
                <div>{conversation.executionCount}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">
                  Message Count
                </div>
                <div>{conversation.messageCount}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">Created</div>
                <div>{formatLocalDateTime(conversation.createTime)}</div>
              </div>
              <div className="space-y-1">
                <div className="text-xs uppercase tracking-wide text-muted-foreground">Updated</div>
                <div>{formatLocalDateTime(conversation.updateTime)}</div>
              </div>
            </CardContent>
          </Card>

          {conversation.errorMessage ? (
            <Card className="border-destructive/50">
              <CardHeader>
                <CardTitle className="text-destructive">Error</CardTitle>
                <CardDescription>
                  Latest terminal error recorded for this conversation.
                </CardDescription>
              </CardHeader>
              <CardContent>
                <div className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive">
                  {conversation.errorMessage}
                </div>
              </CardContent>
            </Card>
          ) : null}

          <Card>
            <CardHeader>
              <CardTitle>Message History</CardTitle>
              <CardDescription>
                {messages.length} of {conversation.messageCount} messages loaded.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <VirtualizedMessageHistory
                messages={messages}
                isInitialLoading={messagesQuery.isLoading}
                isLoadingMore={messagesQuery.isFetchingNextPage}
                hasMore={Boolean(messagesQuery.hasNextPage)}
                error={messagesQuery.error}
                onLoadMore={loadMoreMessages}
              />
            </CardContent>
          </Card>
        </div>
      ) : null}
    </div>
  );
}

function VirtualizedMessageHistory({
  messages,
  isInitialLoading,
  isLoadingMore,
  hasMore,
  error,
  onLoadMore,
}: {
  messages: AiMessage[];
  isInitialLoading: boolean;
  isLoadingMore: boolean;
  hasMore: boolean;
  error: unknown;
  onLoadMore(): void;
}) {
  const scrollRef = React.useRef<HTMLDivElement>(null);
  const hasStatusRow =
    isInitialLoading || isLoadingMore || hasMore || Boolean(error) || messages.length > 0;
  const getItemKey = React.useCallback(
    (index: number) =>
      index < messages.length
        ? (messages[index]?.messageId ?? `message-${index}`)
        : "message-history-status",
    [messages],
  );
  const virtualizer = useVirtualizer({
    count: messages.length + (hasStatusRow ? 1 : 0),
    getScrollElement: () => scrollRef.current,
    estimateSize: () => 220,
    getItemKey,
    overscan: 6,
  });
  const virtualRows = virtualizer.getVirtualItems();
  const lastVirtualIndex = virtualRows.at(-1)?.index ?? -1;

  React.useEffect(() => {
    if (hasMore && !isLoadingMore && lastVirtualIndex >= Math.max(0, messages.length - 5)) {
      onLoadMore();
    }
  }, [hasMore, isLoadingMore, lastVirtualIndex, messages.length, onLoadMore]);

  if (!isInitialLoading && messages.length === 0 && !error) {
    return <div className="text-sm text-muted-foreground">No messages recorded.</div>;
  }

  return (
    <div
      ref={scrollRef}
      className="h-[clamp(20rem,65vh,48rem)] overflow-y-auto rounded-xl border bg-muted/10 agw-scrollbar"
      role="list"
      aria-label="Conversation message history"
    >
      <div className="relative w-full" style={{ height: virtualizer.getTotalSize() }}>
        {virtualRows.map((virtualRow) => {
          const message = messages[virtualRow.index];
          return (
            <div
              key={virtualRow.key}
              ref={virtualizer.measureElement}
              data-index={virtualRow.index}
              role="listitem"
              className="absolute top-0 left-0 w-full p-2"
              style={{ transform: `translateY(${virtualRow.start}px)` }}
            >
              {message ? (
                <HistoryMessage message={message} />
              ) : (
                <div
                  className="flex min-h-16 items-center justify-center px-4 py-3"
                  aria-live="polite"
                >
                  {error ? (
                    <div className="flex flex-wrap items-center justify-center gap-2 text-sm text-destructive">
                      <span>Failed to load more messages: {getApiErrorMessage(error)}</span>
                      <Button size="sm" variant="outline" onClick={onLoadMore}>
                        <RotateCw className="size-3.5" />
                        Retry
                      </Button>
                    </div>
                  ) : isInitialLoading || isLoadingMore || hasMore ? (
                    <div className="flex items-center gap-2 text-sm text-muted-foreground">
                      <LoaderCircle className="size-4 animate-spin" />
                      {isInitialLoading ? "Loading messages…" : "Loading more messages…"}
                    </div>
                  ) : (
                    <div className="text-sm text-muted-foreground">All messages loaded.</div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function HistoryMessage({ message }: { message: AiMessage }) {
  return (
    <div className="rounded-lg border bg-card p-4 shadow-xs">
      <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
        <span className="font-medium text-foreground">
          {message.author || message.role || "Unknown"}
        </span>
        {message.role ? <span>{message.role}</span> : null}
        {message.type ? <span>{message.type}</span> : null}
      </div>

      <div className="mt-3 space-y-2">
        {message.contents.length === 0 ? (
          <div className="text-sm text-muted-foreground">No content recorded.</div>
        ) : (
          message.contents.map((content, index) => (
            <div key={`${message.messageId}:${index}`} className="space-y-1">
              <div className="text-[11px] uppercase tracking-wide text-muted-foreground">
                {content.type}
              </div>
              <pre className="whitespace-pre-wrap break-words rounded-md bg-muted/40 p-3 text-sm">
                {formatMessageContent(content.content)}
              </pre>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
