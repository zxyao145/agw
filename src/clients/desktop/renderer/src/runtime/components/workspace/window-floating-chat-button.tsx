"use client";

import * as React from "react";
import type { AgwFloatingChatHostBridge } from "@desktop/shared/contracts";
import type { FloatingChatButtonRequest } from "@desktop/shared/contracts/floating-chat";

import type { ExtensionHost, ExtensionHostSnapshot } from "../../extensions/extension-host";

declare global {
  interface Window {
    agwFloatingChatHost: AgwFloatingChatHostBridge;
  }
}

/** 当前视图提供上下文，原生按钮覆盖程序客户区。The current view supplies context; the native button spans the app client area. */
export function WindowFloatingChatButton({
  host,
  snapshot,
  hidden,
}: {
  host: ExtensionHost;
  snapshot: ExtensionHostSnapshot;
  hidden: boolean;
}) {
  const previous = React.useRef<{ owner: string | null; open: boolean } | null>(null);
  const focusOwner = React.useRef<string | null>(null);
  const lastRequest = React.useRef<FloatingChatButtonRequest | null>(null);

  React.useEffect(() => {
    if (!snapshot.started) return;
    const bridge = window.agwFloatingChatHost;
    const unsubscribe = bridge.onOpen((owner) => host.openFloatingChatButton(owner));
    return () => {
      unsubscribe();
      if (lastRequest.current) {
        void bridge
          .update(host.hostInstanceId, { ...lastRequest.current, owner: null, focus: false })
          .catch((error: unknown) => console.error("Unable to hide the Chat button.", error));
      }
    };
  }, [host, snapshot.started]);

  React.useEffect(() => {
    if (!snapshot.started) return;
    const button = snapshot.floatingChatButton;
    const open = snapshot.floatingChat?.open === true;
    const visible = !hidden && button !== null && !button.obscured && !open;
    const owner = button?.owner ?? null;
    if (previous.current?.owner !== owner) focusOwner.current = null;
    if (owner !== null && previous.current?.owner === owner && previous.current.open && !open) {
      focusOwner.current = owner;
    }
    const style = getComputedStyle(document.documentElement);
    const request: FloatingChatButtonRequest = {
      owner: visible ? owner : null,
      background: style.getPropertyValue("--popover").trim(),
      foreground: style.getPropertyValue("--popover-foreground").trim(),
      border: style.getPropertyValue("--border").trim(),
      focus: visible && focusOwner.current === owner,
    };
    if (request.focus) focusOwner.current = null;
    previous.current = { owner, open };
    lastRequest.current = request;
    void window.agwFloatingChatHost
      .update(host.hostInstanceId, request)
      .catch((error: unknown) => console.error("Unable to update the Chat button.", error));
  }, [host, snapshot, hidden]);

  return null;
}
