"use client";

import * as React from "react";
import { X } from "lucide-react";

import { Button, Textarea } from "@agw/components";
import { parseSimpleUserInput, type SimpleUserInput } from "@agw/chat-core";
import type { PendingInteraction } from "@agw/chat-runtime";
import { HumanInteractionModeChange } from "./human-interaction-mode-change";
import { HumanInteractionQuestions } from "./human-interaction-questions";

type HumanInteractionPanelProps = {
  request: PendingInteraction & { kind: "user-input" };
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

  const input = parseSimpleUserInput(request.inputKind, request.payload);
  if (input)
    return (
      <SimpleInputPanel
        key={request.interactionId}
        request={request}
        input={input}
        onSubmit={onSubmit}
        onCancel={onCancel}
      />
    );

  return (
    <div className="pointer-events-auto rounded-md border bg-background/95 p-3 shadow-sm backdrop-blur">
      <div className="text-sm font-medium">Unsupported interaction</div>
      <p className="mt-1 text-xs text-muted-foreground">
        This client cannot render the requested {request.inputKind ?? "human"} interaction.
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

function SimpleInputPanel({
  request,
  input,
  onSubmit,
  onCancel,
}: {
  request: PendingInteraction;
  input: SimpleUserInput;
  onSubmit(responseData: unknown): void;
  onCancel(): void;
}) {
  const [value, setValue] = React.useState(input.prefill ?? "");
  return (
    <div className="pointer-events-auto rounded-md border bg-background/95 p-3 shadow-sm backdrop-blur">
      <div className="whitespace-pre-wrap break-words text-sm text-foreground">
        {request.prompt}
      </div>
      {input.message && input.message !== request.prompt ? (
        <p className="mt-1 text-xs text-muted-foreground">{input.message}</p>
      ) : null}
      {input.inputKind === "select" ? (
        <div className="mt-3 grid gap-2">
          {input.options.map((option) => (
            <Button
              key={option}
              type="button"
              variant={value === option ? "default" : "outline"}
              onClick={() => setValue(option)}
            >
              {option}
            </Button>
          ))}
        </div>
      ) : input.inputKind !== "confirm" ? (
        <Textarea
          className="mt-3 min-h-18 resize-none text-sm"
          value={value}
          onChange={(event) => setValue(event.target.value)}
          placeholder={input.placeholder ?? "Response"}
          aria-label={input.placeholder ?? "Response"}
        />
      ) : null}
      <div className="mt-3 flex justify-end gap-2">
        <Button type="button" variant="outline" size="sm" onClick={onCancel}>
          <X className="h-4 w-4" />
          Cancel
        </Button>
        <Button
          type="button"
          size="sm"
          disabled={input.inputKind === "select" && !input.options.includes(value)}
          onClick={() => onSubmit(input.inputKind === "confirm" ? { confirmed: true } : { value })}
        >
          {input.inputKind === "confirm" ? "Confirm" : "Submit"}
        </Button>
      </div>
    </div>
  );
}
