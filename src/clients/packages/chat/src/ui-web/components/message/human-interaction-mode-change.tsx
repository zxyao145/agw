"use client";

import { ArrowRight, Brain, X } from "lucide-react";

import { Button, cn } from "@agw/components";
import type { PendingHumanGate } from "../../../services/execution-hub";
import type {
  HumanInteractionModeChange as ModeChange,
  HumanInteractionModeChangeResponse,
} from "../../../services/human-interaction";

type HumanInteractionModeChangeProps = {
  request: PendingHumanGate & { modeChange: ModeChange };
  embedded?: boolean;
  onSubmit: (response: HumanInteractionModeChangeResponse) => void;
  onCancel: () => void;
};

export function HumanInteractionModeChange({
  request,
  embedded = false,
  onSubmit,
  onCancel,
}: HumanInteractionModeChangeProps) {
  const modeLabel = request.modeChange.mode === "plan" ? "Plan" : "Execute";

  return (
    <section
      className={cn(
        "pointer-events-auto overflow-hidden rounded-xl border bg-gradient-to-br from-background via-background to-muted/35 shadow-lg",
        !embedded && "max-h-[62vh] overflow-auto agw-scrollbar",
      )}
    >
      <div className="flex items-start gap-3 px-4 py-3.5">
        <div className="flex size-9 shrink-0 items-center justify-center rounded-lg border bg-muted/60 text-foreground shadow-sm">
          <Brain className="size-[18px]" />
        </div>
        <div className="min-w-0 flex-1">
          <h2 className="text-sm font-semibold tracking-tight">Change agent mode?</h2>
          <p className="mt-1 text-sm leading-relaxed text-muted-foreground">{request.prompt}</p>
        </div>
      </div>

      <div className="flex justify-end gap-2 border-t bg-background/95 px-4 py-3 backdrop-blur">
        <Button type="button" variant="outline" size="sm" onClick={onCancel}>
          <X className="size-4" />
          Cancel
        </Button>
        <Button
          type="button"
          size="sm"
          aria-label={`Switch to ${modeLabel} mode`}
          onClick={() => onSubmit({ confirmed: true })}
        >
          Switch to {modeLabel}
          <ArrowRight className="size-4" />
        </Button>
      </div>
    </section>
  );
}
