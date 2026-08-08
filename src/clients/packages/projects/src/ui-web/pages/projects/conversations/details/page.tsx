"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useQuery } from "@agw/components/query";

import { getProjectContextDetails, type ContextDetails } from "../../../../../services/task-client";
import { Button } from "@agw/components";
import { ButtonGroup } from "@agw/components";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@agw/components";
import { getApiErrorMessage } from "@agw/api";
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

function getChatHref(projectId: string, conversation: ContextDetails): string {
  const searchParams = new URLSearchParams({
    projectId,
    contextId: conversation.contextId,
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
  const contextId = searchParams.get("contextId") ?? "";

  const conversationQuery = useQuery({
    queryKey: ["projects", projectId, "contexts", contextId],
    queryFn: async () => getProjectContextDetails(projectId, contextId),
  });

  const conversation = conversationQuery.data;
  const messages = conversation?.messages ?? [];

  return (
    <div className="space-y-6 w-full min-w-0 max-w-full">
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
              <span className="font-mono">{conversation.contextId}</span>
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

      {conversationQuery.isLoading ? (
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
              <CardDescription>Messages in this conversation.</CardDescription>
            </CardHeader>
            <CardContent>
              {messages.length === 0 ? (
                <div className="text-sm text-muted-foreground">No messages recorded.</div>
              ) : (
                <div className="space-y-3">
                  {messages.map((message) => (
                    <div key={message.messageId} className="rounded-lg border p-4">
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
                  ))}
                </div>
              )}
            </CardContent>
          </Card>
        </div>
      ) : null}
    </div>
  );
}
