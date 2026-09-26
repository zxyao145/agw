"use client";

import type { InteractionResponse } from "@agw/execution-core";
import type { ConversationTurnInputSummary } from "@agw/projects";

import * as React from "react";
import { CheckCircle2, ChevronRight, CircleAlert, LoaderCircle } from "lucide-react";
import { useWorkSummary } from "@agw/chat-runtime/react";
import { createPortal } from "react-dom";
import { useVirtualizer } from "@tanstack/react-virtual";

import {
  CHAT_BOTTOM_TOLERANCE_PX,
  formatWorkedDuration,
  type ConversationRenderItem,
  type PresentedTool,
  type ToolCallStatus,
} from "@agw/chat-core";
import type { PermissionMode } from "@agw/execution-core";
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  Badge,
  Button,
  Empty,
  EmptyContent,
  EmptyDescription,
  EmptyHeader,
  EmptyTitle,
  cn,
} from "@agw/components";

import { MessageTrigger } from "../message-trigger";
import { AgentflowCheckpointCard } from "./agentflow-checkpoint-card";
import { HumanGateApproval } from "./human-gate-approval";
import { HumanInteractionPanel } from "./human-interaction-panel";
import { HumanInteractionQuestionResultView } from "./human-interaction-question-result";
import { MessageActions } from "./message-actions";
import { PresentedMessageComponent } from "./presented-message";
import { ToolState } from "./tool-state";
import {
  buildUserInputAnchors,
  getActiveUserInputMarkerKey,
  layoutUserInputMarkers,
} from "./user-input-navigation";
import { UserInputNavigator } from "./user-input-navigator";

export type HumanResponseInput = InteractionResponse;

export interface ChatSessionProps {
  items: ConversationRenderItem[];
  conversationKey?: string;
  onWorkSummaryToggle?: () => void;
  onWorkSummaryExpansionChange?: (keys: ReadonlySet<string>) => void;
  scrollElementRef: React.RefObject<HTMLDivElement | null>;
  hasOlderMessages?: boolean;
  isLoadingOlderMessages?: boolean;
  isInitialLoading?: boolean;
  onLoadOlderMessages?: () => void;
  onAutoLoadOlderMessages?: () => void;
  autoLoadBlockedSummaryKey?: string | null;
  userInputNavigationHost?: HTMLDivElement | null;
  onUserInputNavigate?: () => void;
  userInputs?: readonly ConversationTurnInputSummary[];
  onLoadUserInput?: (key: string) => Promise<boolean>;
  permissionMode?: PermissionMode;
  showCheckpointResume?: boolean;
  checkpointResumeDisabled?: boolean;
  onCheckpointResume?: (occurrenceId: string) => void;
  onHumanResponse?: (response: HumanResponseInput) => void;
}

export function Conversation({
  items: renderItems,
  conversationKey,
  onWorkSummaryToggle,
  onWorkSummaryExpansionChange,
  scrollElementRef,
  hasOlderMessages = false,
  isLoadingOlderMessages = false,
  isInitialLoading = false,
  onLoadOlderMessages,
  onAutoLoadOlderMessages,
  autoLoadBlockedSummaryKey,
  userInputNavigationHost = null,
  onUserInputNavigate,
  userInputs,
  onLoadUserInput,
  permissionMode,
  showCheckpointResume = false,
  checkpointResumeDisabled = false,
  onCheckpointResume,
  onHumanResponse,
}: ChatSessionProps) {
  const {
    rows: items,
    expandedKeys,
    toggleWorkSummary,
  } = useWorkSummary(renderItems, conversationKey);
  React.useEffect(() => {
    onWorkSummaryExpansionChange?.(expandedKeys);
  }, [expandedKeys, onWorkSummaryExpansionChange]);
  React.useEffect(() => {
    const scrollContainer = scrollElementRef.current;
    if (
      !scrollContainer ||
      !hasOlderMessages ||
      isLoadingOlderMessages ||
      !onAutoLoadOlderMessages ||
      (autoLoadBlockedSummaryKey && !expandedKeys.has(autoLoadBlockedSummaryKey))
    ) {
      return;
    }
    const frame = requestAnimationFrame(() => {
      if (scrollContainer.scrollHeight <= scrollContainer.clientHeight + 1) {
        onAutoLoadOlderMessages();
      }
    });
    return () => cancelAnimationFrame(frame);
  }, [
    autoLoadBlockedSummaryKey,
    expandedKeys,
    hasOlderMessages,
    isLoadingOlderMessages,
    items.length,
    onAutoLoadOlderMessages,
    scrollElementRef,
  ]);
  const hasHistoryLoader = hasOlderMessages || isLoadingOlderMessages;
  const rowOffset = hasHistoryLoader ? 1 : 0;
  const getItemKey = React.useCallback(
    (index: number) =>
      index === 0 && hasHistoryLoader
        ? "older-message-loader"
        : (items[index - rowOffset]?.key ?? `conversation-row-${index}`),
    [hasHistoryLoader, items, rowOffset],
  );
  const virtualizer = useVirtualizer({
    count: items.length + rowOffset,
    getScrollElement: () => scrollElementRef.current,
    estimateSize: () => 72,
    getItemKey,
    overscan: 6,
    useFlushSync: false,
  });
  const virtualRows = virtualizer.getVirtualItems();
  const viewportHeight = virtualizer.scrollRect?.height ?? 0;
  const totalSize = virtualizer.getTotalSize() + 20;
  const navigationHeight = Math.max(viewportHeight - 168, 0);
  const userInputAnchors = React.useMemo(
    () => (userInputNavigationHost ? buildUserInputAnchors(items, userInputs) : []),
    [items, userInputNavigationHost, userInputs],
  );
  const userInputMarkers = userInputNavigationHost
    ? layoutUserInputMarkers(userInputAnchors, virtualizer.measurementsCache, rowOffset)
    : [];
  const scrollElement = scrollElementRef.current;
  const isAtBottom =
    scrollElement !== null &&
    scrollElement.scrollHeight - scrollElement.scrollTop - scrollElement.clientHeight <=
      CHAT_BOTTOM_TOLERANCE_PX;
  const activeUserInputKey = getActiveUserInputMarkerKey(
    userInputMarkers,
    virtualizer.scrollOffset ?? scrollElement?.scrollTop ?? 0,
    isAtBottom,
  );
  const showNavigation =
    userInputNavigationHost !== null &&
    userInputMarkers.length > 0 &&
    navigationHeight > 0 &&
    (totalSize > viewportHeight + 1 ||
      userInputAnchors.some((anchor) => anchor.itemIndex === null));

  const [pendingNavigation, setPendingNavigation] = React.useState<{
    key: string;
    conversationKey: string | undefined;
  } | null>(null);
  const pendingRowIndex =
    pendingNavigation?.conversationKey === conversationKey
      ? userInputMarkers.find((marker) => marker.key === pendingNavigation?.key)?.rowIndex
      : null;
  React.useEffect(() => setPendingNavigation(null), [conversationKey]);
  React.useLayoutEffect(() => {
    if (pendingRowIndex == null) return;
    virtualizer.scrollToIndex(pendingRowIndex, { align: "start", behavior: "auto" });
    setPendingNavigation(null);
  }, [pendingRowIndex, virtualizer]);

  const handleUserInputSelect = React.useCallback(
    (key: string) => {
      onUserInputNavigate?.();
      const navigation = { key, conversationKey };
      setPendingNavigation(navigation);
      if (userInputAnchors.find((anchor) => anchor.key === key)?.itemIndex != null) return;
      // 历史没有加载到目标输入时撤销这次跳转，之后再加载到它也不会跳转。
      // Cancel this jump when history does not reach the target input, so a later load does not jump to it.
      void onLoadUserInput?.(key).then((loaded) => {
        if (!loaded) setPendingNavigation((current) => (current === navigation ? null : current));
      });
    },
    [conversationKey, onLoadUserInput, onUserInputNavigate, userInputAnchors],
  );

  if (items.length === 0 && isInitialLoading) {
    return (
      <Empty>
        <EmptyHeader>
          <LoaderCircle className="h-5 w-5 animate-spin text-muted-foreground" />
          <EmptyTitle>Loading conversation...</EmptyTitle>
        </EmptyHeader>
      </Empty>
    );
  }

  if (items.length === 0 && !hasHistoryLoader) {
    return (
      <Empty>
        <EmptyHeader>
          <EmptyTitle>No Message Yet</EmptyTitle>
          <EmptyDescription>There are currently no messages.</EmptyDescription>
        </EmptyHeader>
        <EmptyContent className="flex-row justify-center gap-2" />
      </Empty>
    );
  }

  return (
    <div className="w-full flex-1">
      {showNavigation && userInputNavigationHost
        ? createPortal(
            <div className="absolute top-6 left-0">
              <UserInputNavigator
                markers={userInputMarkers}
                activeKey={activeUserInputKey}
                height={navigationHeight}
                onSelect={handleUserInputSelect}
              />
            </div>,
            userInputNavigationHost,
          )
        : null}
      <div
        className="agw-conversation-list"
        style={{ height: totalSize }}
        role="list"
        aria-label="Conversation messages"
      >
        {virtualRows.map((virtualRow) => {
          const isLoader = hasHistoryLoader && virtualRow.index === 0;
          const item = isLoader ? null : items[virtualRow.index - rowOffset];

          return (
            <div
              key={virtualRow.key}
              ref={virtualizer.measureElement}
              data-index={virtualRow.index}
              role="listitem"
              className="agw-msg-item"
              style={{ transform: `translateY(${virtualRow.start}px)` }}
            >
              {isLoader ? (
                <div className="flex justify-center px-4 py-2" aria-live="polite">
                  <Button
                    type="button"
                    size="sm"
                    variant="ghost"
                    className="rounded-full text-xs text-muted-foreground"
                    disabled={isLoadingOlderMessages}
                    onClick={onLoadOlderMessages}
                  >
                    {isLoadingOlderMessages ? (
                      <LoaderCircle className="size-3.5 animate-spin" />
                    ) : null}
                    {isLoadingOlderMessages ? "Loading earlier messages…" : "Load earlier messages"}
                  </Button>
                </div>
              ) : item ? (
                <ConversationItem
                  item={item}
                  workExpanded={expandedKeys.has(item.key)}
                  onWorkToggle={() => {
                    onWorkSummaryToggle?.();
                    toggleWorkSummary(item.key);
                  }}
                  permissionMode={permissionMode}
                  showCheckpointResume={showCheckpointResume}
                  checkpointResumeDisabled={checkpointResumeDisabled}
                  onCheckpointResume={onCheckpointResume}
                  onHumanResponse={onHumanResponse}
                />
              ) : null}
            </div>
          );
        })}
      </div>
    </div>
  );
}

function ConversationItem({
  item,
  workExpanded,
  onWorkToggle,
  permissionMode,
  showCheckpointResume,
  checkpointResumeDisabled,
  onCheckpointResume,
  onHumanResponse,
}: {
  item: ConversationRenderItem;
  workExpanded: boolean;
  onWorkToggle: () => void;
  permissionMode?: PermissionMode;
  showCheckpointResume: boolean;
  checkpointResumeDisabled: boolean;
  onCheckpointResume?: (occurrenceId: string) => void;
  onHumanResponse?: (response: HumanResponseInput) => void;
}) {
  if (item.type === "work-summary") {
    return (
      <div className="mx-4 mt-6 border-b border-border/60 pb-1">
        <button
          type="button"
          aria-expanded={workExpanded}
          onClick={onWorkToggle}
          className="flex min-h-11 cursor-pointer items-center gap-1.5 rounded-sm text-sm text-muted-foreground transition-colors hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-ring"
        >
          {item.name ? (
            <>
              <span className="min-w-0 truncate font-medium text-foreground/70">
                {item.name}
              </span>{" "}
            </>
          ) : null}
          {formatWorkedDuration(item.durationMs)}
          <ChevronRight
            aria-hidden="true"
            className={cn("size-4 transition-transform", workExpanded && "rotate-90")}
          />
        </button>
      </div>
    );
  }

  if (item.type === "checkpoint") {
    return (
      <AgentflowCheckpointCard
        name={item.checkpoint.name}
        showResume={showCheckpointResume}
        available={item.availability?.available === true}
        disabled={checkpointResumeDisabled || !onCheckpointResume}
        onResume={() => onCheckpointResume?.(item.checkpoint.occurrenceId)}
      />
    );
  }

  if (item.type === "human-interaction-result") {
    return (
      <div className="mx-4 max-w-full">
        <HumanInteractionQuestionResultView result={item.result} />
      </div>
    );
  }

  if (item.type === "human-interaction") {
    const request = item.request;
    if (request.kind === "user-input") {
      return (
        <div className="mx-4 max-w-full">
          <div className="mb-2 flex items-center gap-2 px-1">
            <Badge variant="secondary" className="text-xs">
              {request.source.toolName ?? "Function"}
            </Badge>
            <span className="text-xs text-muted-foreground">Waiting for your input</span>
          </div>
          <HumanInteractionPanel
            request={request}
            embedded={item.embedded}
            onSubmit={(responseData) =>
              onHumanResponse?.({
                kind: "user-input",
                interactionId: request.interactionId,
                cancelled: false,
                responseData,
              })
            }
            onCancel={() =>
              onHumanResponse?.({
                kind: "user-input",
                interactionId: request.interactionId,
                cancelled: true,
              })
            }
          />
        </div>
      );
    }

    return (
      <div className="mx-4 max-w-full">
        <HumanGateApproval
          request={request}
          permissionMode={permissionMode}
          onApprove={(scope, responseText) =>
            onHumanResponse?.(
              request.kind === "tool-approval"
                ? {
                    kind: request.kind,
                    interactionId: request.interactionId,
                    approved: true,
                    scope,
                  }
                : {
                    kind: request.kind,
                    interactionId: request.interactionId,
                    approved: true,
                    responseText,
                  },
            )
          }
          onReject={(responseText) =>
            onHumanResponse?.(
              request.kind === "tool-approval"
                ? {
                    kind: request.kind,
                    interactionId: request.interactionId,
                    approved: false,
                    scope: "Once",
                  }
                : {
                    kind: request.kind,
                    interactionId: request.interactionId,
                    approved: false,
                    responseText,
                  },
            )
          }
        />
      </div>
    );
  }

  if (item.type === "tool-accordion") {
    return (
      <div className="mx-4 min-w-0 w-full max-w-[80%]">
        <ToolAccordionRow tool={item} />
      </div>
    );
  }

  if (item.type === "tool-batch") {
    return (
      <div className="mx-4 min-w-0 w-full max-w-[80%]">
        <ToolBatch tools={item.tools} batchKey={item.key} />
      </div>
    );
  }

  if (item.type === "tool-state") {
    return (
      <div className="mx-4 max-w-[80%]">
        <ToolState message={item.message} />
      </div>
    );
  }

  const message = item.message;
  return (
    <div
      className={cn(
        "group/message mx-4 max-w-full",
        item.type === "result" &&
          (item.hasWorkSummary ? "pt-2" : "mt-8 border-t border-dashed pt-4"),
      )}
      data-msg-id={message.source.messageId}
    >
      {message.meta ? (
        <div
          className={cn(
            "mb-1 flex max-w-[80%] items-center gap-1.5 text-xs text-muted-foreground",
            message.alignment === "right" && "ml-auto justify-end",
          )}
        >
          {message.meta.name ? (
            <span className="min-w-0 truncate font-medium text-foreground/70">
              {message.meta.name}
            </span>
          ) : null}
          {message.meta.name && message.meta.author ? (
            <span className="shrink-0 text-muted-foreground/60">/</span>
          ) : null}
          {message.meta.author ? (
            <span className="min-w-0 truncate font-mono text-[11px]">{message.meta.author}</span>
          ) : null}
          {(message.meta.name || message.meta.author) && message.meta.model ? (
            <span className="shrink-0 text-muted-foreground/60">/</span>
          ) : null}
          {message.meta.model ? (
            <span
              className="min-w-0 truncate font-mono text-[11px] text-muted-foreground/80"
              title={message.meta.model}
            >
              {message.meta.model}
            </span>
          ) : null}
        </div>
      ) : null}
      <PresentedMessageComponent message={message} />
      {item.type === "result" || message.alignment === "right" ? (
        <MessageActions message={message} />
      ) : null}
    </div>
  );
}

function ToolBatch({ tools, batchKey }: { tools: PresentedTool[]; batchKey: string }) {
  const counts = tools.reduce(
    (result, tool) => {
      result[tool.status] += 1;
      return result;
    },
    { running: 0, complete: 0, failed: 0 } as Record<ToolCallStatus, number>,
  );
  const toolSummary = summarizeToolNames(tools);

  return (
    <Accordion type="single" collapsible className="min-w-0 w-full">
      <AccordionItem value={batchKey} className="rounded-lg border bg-card px-3 shadow-xs">
        <MessageTrigger className="min-w-0 cursor-pointer py-2">
          <div className="flex w-0 min-w-0 flex-1 flex-col gap-1.5 overflow-hidden sm:flex-row sm:items-center">
            <div className="min-w-0 flex-1 overflow-hidden">
              <div className="text-sm font-medium">{tools.length} tool calls</div>
              <div className="block max-w-full truncate text-xs text-muted-foreground">
                {toolSummary}
              </div>
            </div>
            <div className="flex flex-wrap items-center gap-1.5 sm:shrink-0">
              {counts.running > 0 ? (
                <ToolStatusBadge status="running" count={counts.running} />
              ) : null}
              {counts.failed > 0 ? <ToolStatusBadge status="failed" count={counts.failed} /> : null}
              {counts.complete > 0 ? (
                <ToolStatusBadge status="complete" count={counts.complete} />
              ) : null}
            </div>
          </div>
        </MessageTrigger>
        <AccordionContent className="px-1">
          <div className="space-y-1.5 border-l border-border/60 pl-3">
            {tools.map((tool) => (
              <ToolAccordionRow key={tool.identity} tool={tool} />
            ))}
          </div>
        </AccordionContent>
      </AccordionItem>
    </Accordion>
  );
}

function ToolAccordionRow({ tool }: { tool: PresentedTool }) {
  return (
    <Accordion type="single" collapsible className="min-w-0 w-full">
      <AccordionItem value={tool.identity}>
        <MessageTrigger className="min-w-0 cursor-pointer py-1.5">
          <div className="flex w-0 min-w-0 flex-1 items-center gap-2 overflow-hidden">
            <Badge variant="secondary" className="text-xs">
              {tool.toolName}
            </Badge>
            {tool.summary ? (
              <span className="block min-w-0 max-w-full flex-1 truncate text-xs text-muted-foreground">
                {tool.summary}
              </span>
            ) : null}
            <span className="ml-auto shrink-0">
              <ToolStatusBadge status={tool.status} />
            </span>
          </div>
        </MessageTrigger>
        <AccordionContent className="px-1">
          {tool.messages.length > 0 ? (
            <div className="space-y-3 pl-6 pr-1">
              {tool.messages.map((message) => (
                <PresentedMessageComponent key={message.identity} message={message} embedded />
              ))}
            </div>
          ) : (
            <p className="pl-6 text-xs text-muted-foreground">No tool details yet.</p>
          )}
        </AccordionContent>
      </AccordionItem>
    </Accordion>
  );
}

function ToolStatusBadge({ status, count }: { status: ToolCallStatus; count?: number }) {
  const presentation = {
    running: {
      label: count ? `${count} running` : "Running",
      icon: LoaderCircle,
      variant: "outline" as const,
      className: "text-primary",
    },
    complete: {
      label: count ? `${count} done` : "Done",
      icon: CheckCircle2,
      variant: "secondary" as const,
      className: "",
    },
    failed: {
      label: count ? `${count} failed` : "Failed",
      icon: CircleAlert,
      variant: "destructive" as const,
      className: "",
    },
  }[status];
  const Icon = presentation.icon;

  return (
    <Badge variant={presentation.variant} className={presentation.className}>
      <Icon className={status === "running" ? "animate-spin" : undefined} aria-hidden="true" />
      {presentation.label}
    </Badge>
  );
}

function summarizeToolNames(tools: PresentedTool[]): string {
  const counts = new Map<string, number>();
  for (const tool of tools) {
    counts.set(tool.toolName, (counts.get(tool.toolName) ?? 0) + 1);
  }

  return [...counts.entries()].map(([toolName, count]) => `${toolName} ×${count}`).join(" · ");
}
