"use client";

import * as React from "react";
import {
  Button,
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  formatLocalTimeExact,
  parseApiDateTime,
} from "@agw/components";
import { getMessageCopyText, type PresentedMessage } from "@agw/chat-core";
import { Check, Copy, MoreHorizontal } from "lucide-react";
import { toast } from "sonner";
import type { ChatMessageActionArgs, ExtensionMenuItem } from "./chat-extension-types";

export function MessageActions({
  message,
  actions,
}: {
  message: PresentedMessage;
  /** 扩展菜单项，非空时在 Copy 旁显示 More 菜单。Extension items shown in a More menu beside Copy when non-empty. */
  actions?: readonly ExtensionMenuItem<ChatMessageActionArgs>[];
}) {
  const [copied, setCopied] = React.useState(false);
  const [time, setTime] = React.useState<string | null>(null);
  const resetTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const text = getMessageCopyText(message.contents);
  const createdAt = message.source.createdAt;
  const label = copied ? "Message copied" : "Copy message";

  React.useEffect(() => {
    const date = createdAt ? parseApiDateTime(createdAt) : null;
    setTime(date ? formatLocalTimeExact(date).slice(0, 5) : null);
  }, [createdAt]);

  React.useEffect(
    () => () => {
      if (resetTimerRef.current) clearTimeout(resetTimerRef.current);
    },
    [],
  );

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      if (resetTimerRef.current) clearTimeout(resetTimerRef.current);
      resetTimerRef.current = setTimeout(() => setCopied(false), 2_000);
    } catch {
      setCopied(false);
      toast.error("Unable to copy message");
    }
  };

  const handleActionSelect = (action: ExtensionMenuItem<ChatMessageActionArgs>) => {
    const turnId = message.source.additionalProperties?.turnId;
    action.onSelect({
      messageId: message.source.messageId,
      role: message.source.role ?? null,
      text,
      turnId: typeof turnId === "string" ? turnId : null,
    });
  };

  // 菜单打开时焦点在 Portal 中，用 data-state 保持操作按钮可见。
  // While the menu is open focus sits in its portal, so data-state keeps the actions visible.
  return (
    <div
      className="agw-msg-actions pointer-events-none
     flex h-8 items-center justify-end gap-2 whitespace-nowrap
     text-xs text-muted-foreground opacity-0
     transition-opacity group-hover/message:pointer-events-auto group-hover/message:opacity-100
     group-focus-within/message:pointer-events-auto group-focus-within/message:opacity-100
     has-[[data-state=open]]:pointer-events-auto has-[[data-state=open]]:opacity-100
     motion-reduce:transition-none"
    >
      {time ? <time dateTime={createdAt ?? undefined}>{time}</time> : null}
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        className="size-6 text-muted-foreground hover:text-foreground"
        aria-label={label}
        title={label}
        disabled={!text}
        onClick={handleCopy}
      >
        {copied ? (
          <Check className="size-3.5" aria-hidden="true" />
        ) : (
          <Copy className="size-3.5" aria-hidden="true" />
        )}
      </Button>
      {actions && actions.length > 0 ? (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              className="size-6 text-muted-foreground hover:text-foreground"
              aria-label="More message actions"
              title="More message actions"
            >
              <MoreHorizontal className="size-3.5" aria-hidden="true" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            {actions.map((action) => (
              <DropdownMenuItem
                key={action.id}
                className="cursor-pointer"
                onSelect={() => handleActionSelect(action)}
              >
                {action.icon}
                {action.title}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      ) : null}
    </div>
  );
}
