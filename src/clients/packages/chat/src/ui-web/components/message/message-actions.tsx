"use client";

import * as React from "react";
import { Button, formatLocalTimeExact, parseApiDateTime } from "@agw/components";
import { getMessageCopyText, type PresentedMessage } from "@agw/chat-core";
import { Check, Copy } from "lucide-react";
import { toast } from "sonner";

export function MessageActions({ message }: { message: PresentedMessage }) {
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

  return (
    <div className="agw-msg-actions pointer-events-none absolute right-0 top-full flex h-8 items-center gap-2 whitespace-nowrap text-xs text-muted-foreground opacity-0 transition-opacity group-hover/message:pointer-events-auto group-hover/message:opacity-100 group-focus-within/message:pointer-events-auto group-focus-within/message:opacity-100 motion-reduce:transition-none">
      {time ? <time dateTime={createdAt ?? undefined}>{time}</time> : null}
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        className="size-8 text-muted-foreground hover:text-foreground"
        aria-label={label}
        title={label}
        disabled={!text}
        onClick={handleCopy}
      >
        {copied ? (
          <Check className="size-4" aria-hidden="true" />
        ) : (
          <Copy className="size-4" aria-hidden="true" />
        )}
      </Button>
    </div>
  );
}
