"use client";

import * as React from "react";
import { Button, Textarea } from "@agw/components";
import { buildWorkflowGateResponse, type PendingInteraction } from "@agw/chat-core";
import type { InteractionResponse } from "@agw/execution-core";

type WorkflowGateCardProps = {
  request: Extract<PendingInteraction, { kind: "workflow-gate" }>;
  onRespond: (response: Extract<InteractionResponse, { kind: "workflow-gate" }>) => void;
};

export function WorkflowGateCard({ request, onRespond }: WorkflowGateCardProps) {
  const [responseText, setResponseText] = React.useState("");

  return (
    <div className="pointer-events-auto space-y-2 rounded-md border bg-background/95 p-3 shadow-sm">
      <div className="text-sm font-medium">{request.source.nodeName || "Human Gate"}</div>
      <p className="whitespace-pre-wrap break-words text-sm">{request.prompt}</p>
      {request.inputPreview && (
        <p className="whitespace-pre-wrap break-words rounded-md bg-muted px-2 py-1.5 text-sm text-muted-foreground">
          {request.inputPreview}
        </p>
      )}
      {request.allowResponseText && (
        <Textarea
          aria-label="Response"
          placeholder="Response"
          value={responseText}
          onChange={(event) => setResponseText(event.target.value)}
          className="min-h-18 text-sm"
        />
      )}
      <div className="flex flex-wrap justify-end gap-2">
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={() => onRespond(buildWorkflowGateResponse(request))}
        >
          Stop workflow
        </Button>
        {request.choices.map((choice, index) => (
          <Button
            key={choice.choiceId}
            type="button"
            variant={index === 0 ? "default" : "outline"}
            size="sm"
            onClick={() =>
              onRespond(buildWorkflowGateResponse(request, choice.choiceId, responseText))
            }
          >
            {choice.label}
          </Button>
        ))}
      </div>
    </div>
  );
}
