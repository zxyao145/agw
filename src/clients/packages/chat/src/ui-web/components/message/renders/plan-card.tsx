"use client";

import * as React from "react";
import { Button } from "@agw/components";
import { Check, Copy, Brain } from "lucide-react";
import { toast } from "sonner";
import type { ProposedPlanPresentation } from "../types";
import MdCard from "./md-card";

const COPY_STATE_DURATION_MS = 2_000;

export default function PlanCard({
  leadingMarkdown,
  markdown,
  trailingMarkdown,
}: ProposedPlanPresentation) {
  const headingId = React.useId();
  const resetTimerRef = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const [copied, setCopied] = React.useState(false);
  const normalizedMarkdown = markdown.trim();

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
      await navigator.clipboard.writeText(normalizedMarkdown);
      setCopied(true);
      if (resetTimerRef.current) {
        clearTimeout(resetTimerRef.current);
      }
      resetTimerRef.current = setTimeout(() => setCopied(false), COPY_STATE_DURATION_MS);
    } catch {
      toast.error("Unable to copy plan");
    }
  };

  return (
    <>
      {leadingMarkdown ? (
        <div className="msg-content mb-4">
          <MdCard mdText={leadingMarkdown} />
        </div>
      ) : null}
      <section
        aria-labelledby={headingId}
        className="w-full overflow-hidden px-4 sm:px-5 rounded-2xl border border-border/70 bg-card text-card-foreground shadow-xs"
      >
        <header className="flex items-center justify-between gap-4 pt-4 pb-2 sm:pt-5">
          <div className="flex min-w-0 items-center gap-2 text-muted-foreground">
            <Brain className="size-4.5 shrink-0" strokeWidth={1.75} aria-hidden="true" />
            <h2 id={headingId} className="truncate text-sm font-medium">
              Plan
            </h2>
          </div>
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="size-8 shrink-0 rounded-lg text-muted-foreground hover:text-foreground"
            aria-label={copied ? "Plan copied" : "Copy plan"}
            disabled={!normalizedMarkdown}
            onClick={handleCopy}
          >
            {copied ? (
              <Check className="size-4" aria-hidden="true" />
            ) : (
              <Copy className="size-4" aria-hidden="true" />
            )}
          </Button>
        </header>
        <div className="msg-content pt-3 pb-5 text-[15px] leading-7 sm:pt-4 sm:pb-6">
          <MdCard mdText={markdown} />
        </div>
      </section>
      {trailingMarkdown ? (
        <div className="msg-content mt-4">
          <MdCard mdText={trailingMarkdown} />
        </div>
      ) : null}
    </>
  );
}
