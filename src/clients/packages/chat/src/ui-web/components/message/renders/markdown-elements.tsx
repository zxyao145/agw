"use client";

import { Button } from "@agw/components";
import { Check, Copy, ExternalLink, FileText, WrapText } from "lucide-react";
import React from "react";
import { toast } from "sonner";
import { ConversationImage } from "../conversation-image";

// Markdown 渲染实现共用的显示部件。Display parts shared by the Markdown renderers.

const COPY_STATE_DURATION_MS = 2_000;

export function isHttpLink(href: string | undefined): href is string {
  if (!href) return false;
  try {
    const url = new URL(href);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}

export function MarkdownCodeBlock({
  code,
  language,
  children,
}: React.PropsWithChildren<{ code: string; language: string }>) {
  const resetTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const [isWrapped, setIsWrapped] = React.useState(true);
  const [copied, setCopied] = React.useState(false);
  const wrapLabel = isWrapped ? "Disable word wrap" : "Enable word wrap";
  const copyLabel = copied ? "Code copied" : "Copy code";

  React.useEffect(
    () => () => {
      if (resetTimerRef.current) {
        clearTimeout(resetTimerRef.current);
      }
    },
    [],
  );

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(code);
      setCopied(true);
      if (resetTimerRef.current) {
        clearTimeout(resetTimerRef.current);
      }
      resetTimerRef.current = setTimeout(() => setCopied(false), COPY_STATE_DURATION_MS);
    } catch {
      toast.error("Unable to copy code");
    }
  };

  return (
    <div className="msg-content-md-code-block">
      <div className="msg-content-md-code-header">
        <span className="min-w-0 truncate font-medium">{language}</span>
        <div className="flex shrink-0 items-center gap-0.5">
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className={
              "size-6 rounded-md text-[#71757a] hover:bg-black/5 hover:text-[#17191d] dark:text-[#aeb2b8] dark:hover:bg-white/10 dark:hover:text-white" +
              (isWrapped ? " bg-black/5 text-[#17191d] dark:bg-white/10 dark:text-white" : "")
            }
            aria-label={wrapLabel}
            aria-pressed={isWrapped}
            title={wrapLabel}
            onClick={() => setIsWrapped((current) => !current)}
          >
            <WrapText className="size-3.5" aria-hidden="true" />
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="size-6 rounded-md text-[#71757a] hover:bg-black/5 hover:text-[#17191d] dark:text-[#aeb2b8] dark:hover:bg-white/10 dark:hover:text-white"
            aria-label={copyLabel}
            title={copyLabel}
            disabled={!code}
            onClick={handleCopy}
          >
            {copied ? (
              <Check className="size-3.5" aria-hidden="true" />
            ) : (
              <Copy className="size-3.5" aria-hidden="true" />
            )}
          </Button>
        </div>
      </div>
      <pre className="msg-content-md-code-block-body agw-scrollbar" data-wrap={isWrapped}>
        {children}
      </pre>
    </div>
  );
}

export function MarkdownLink({
  href,
  title,
  children,
}: React.PropsWithChildren<{ href?: string; title?: string | null }>) {
  return isHttpLink(href) ? (
    <a
      href={href}
      title={title ?? undefined}
      target="_blank"
      rel="noreferrer"
      className="hover:underline focus-visible:underline"
    >
      {children}
      <ExternalLink
        aria-hidden="true"
        className="ml-1 inline-block size-[0.85em] align-[-0.06em]"
      />
    </a>
  ) : (
    <span title={title ?? href} className="text-(color:--agw-prose-link)">
      <FileText aria-hidden="true" className="mr-1 inline-block size-[0.9em] align-[-0.08em]" />
      {children}
    </span>
  );
}

export function MarkdownImage({
  src,
  alt,
  title,
}: {
  src?: unknown;
  alt?: string | null;
  title?: string | null;
}) {
  return typeof src === "string" && src ? (
    <ConversationImage
      src={src}
      alt={alt || "Markdown image"}
      title={title ?? undefined}
      name={alt ?? undefined}
    />
  ) : null;
}

export function MarkdownTable({ children }: React.PropsWithChildren) {
  return (
    <div
      className="msg-content-md-table-wrap agw-scrollbar"
      role="region"
      aria-label="Scrollable table"
      tabIndex={0}
    >
      <table className="msg-content-md-table">{children}</table>
    </div>
  );
}
