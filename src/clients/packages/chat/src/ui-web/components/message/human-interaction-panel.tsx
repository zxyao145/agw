"use client";

import { X } from "lucide-react";

import { Button } from "@agw/components";
import type { PendingHumanGate } from "../../../services/execution-hub";
import { HumanInteractionModeChange } from "./human-interaction-mode-change";
import { HumanInteractionQuestions } from "./human-interaction-questions";

type HumanInteractionPanelProps = {
  request: PendingHumanGate & { requestType: "human-interaction" };
  embedded?: boolean;
  onSubmit: (responseData: unknown) => void;
  onCancel: () => void;
};

export function HumanInteractionPanel({
  request,
  embedded = false,
  onSubmit,
  onCancel,
}: HumanInteractionPanelProps) {
  if (request.modeChange) {
    return (
      <HumanInteractionModeChange
        request={{ ...request, modeChange: request.modeChange }}
        embedded={embedded}
        onSubmit={onSubmit}
        onCancel={onCancel}
      />
    );
  }

  if (request.questions) {
    return (
      <HumanInteractionQuestions
        request={{ ...request, questions: request.questions }}
        embedded={embedded}
        onSubmit={onSubmit}
        onCancel={onCancel}
      />
    );
  }

  return (
    <div className="pointer-events-auto rounded-md border bg-background/95 p-3 shadow-sm backdrop-blur">
      <div className="text-sm font-medium">Unsupported interaction</div>
      <p className="mt-1 text-xs text-muted-foreground">
        This client cannot render the requested {request.interactionKind ?? "human"} interaction.
      </p>
      <div className="mt-3 flex justify-end">
        <Button type="button" variant="outline" size="sm" onClick={onCancel}>
          <X className="h-4 w-4" />
          Cancel request
        </Button>
      </div>
    </div>
  );
}
