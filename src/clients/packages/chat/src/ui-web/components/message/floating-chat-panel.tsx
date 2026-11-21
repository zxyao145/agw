"use client";

import * as React from "react";
import { X } from "lucide-react";

import { Button } from "@agw/components";
import { cn } from "@agw/components";

export type FloatingChatPanelMode = "page" | "floating" | "hidden";

export interface FloatingChatPanelProps {
  mode: FloatingChatPanelMode;
  onClose: () => void;
  onNewConversation?: () => void;
  returnFocusRef?: React.RefObject<HTMLElement | null>;
  children: React.ReactNode;
}

function isVisibleFocusTarget(element: HTMLElement): boolean {
  if (!element.isConnected) return false;
  for (let current: HTMLElement | null = element; current; current = current.parentElement) {
    const style = getComputedStyle(current);
    if (
      current.hidden ||
      current.inert ||
      current.getAttribute("aria-hidden") === "true" ||
      style.display === "none" ||
      style.visibility === "hidden" ||
      style.opacity === "0"
    ) {
      return false;
    }
  }
  return true;
}

export function FloatingChatPanel({
  mode,
  onClose,
  onNewConversation,
  returnFocusRef,
  children,
}: FloatingChatPanelProps) {
  const panelRef = React.useRef<HTMLDivElement>(null);
  const wasFloatingRef = React.useRef(mode === "floating");

  React.useEffect(() => {
    if (mode !== "floating") return;
    panelRef.current?.querySelector<HTMLElement>("textarea[role='combobox']")?.focus();
  }, [mode]);

  React.useEffect(() => {
    if (wasFloatingRef.current && mode !== "floating") {
      const element = returnFocusRef?.current;
      if (element && isVisibleFocusTarget(element)) element.focus();
    }
    wasFloatingRef.current = mode === "floating";
  }, [mode, returnFocusRef]);

  const handleKeyDown = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (mode === "floating" && event.key === "Escape") {
      event.preventDefault();
      onClose();
    }
  };

  return (
    <div
      ref={panelRef}
      data-slot={mode === "floating" ? "floating-chat-panel" : undefined}
      aria-hidden={mode === "hidden"}
      inert={mode === "hidden"}
      onKeyDown={handleKeyDown}
      className={cn(
        "min-h-0",
        mode === "page" && "relative flex min-h-0 flex-1 flex-col",
        mode === "floating" &&
          "absolute right-0 top-0 z-30 flex h-full w-[min(30rem,100%)] min-h-0 flex-col overflow-hidden rounded-lg border bg-background shadow-xl",
        mode === "hidden" && "invisible pointer-events-none absolute inset-0",
      )}
    >
      {mode === "floating" ? (
        <div className="flex h-11 shrink-0 items-center justify-between border-b px-3">
          <span className="text-sm font-semibold">Chat</span>
          <div className="flex items-center gap-1">
            {onNewConversation ? (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                aria-label="New conversation"
                onClick={onNewConversation}
              >
                new
              </Button>
            ) : null}
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              aria-label="Close chat"
              onClick={onClose}
            >
              <X className="size-4" />
            </Button>
          </div>
        </div>
      ) : null}
      <div className="flex min-h-0 flex-1 flex-col">{children}</div>
    </div>
  );
}
