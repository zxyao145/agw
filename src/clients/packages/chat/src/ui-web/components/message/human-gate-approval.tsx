"use client";

import * as React from "react";
import { Check, ShieldCheck, X } from "lucide-react";

import type { PendingHumanGate } from "../../../services/execution-hub";
import { Button } from "@agw/components";
import { Badge } from "@agw/components";
import { Textarea } from "@agw/components";

type HumanGateApprovalProps = {
  request: PendingHumanGate;
  onApprove: (responseText?: string) => void;
  onReject: (responseText?: string) => void;
};

export function HumanGateApproval({ request, onApprove, onReject }: HumanGateApprovalProps) {
  const [responseText, setResponseText] = React.useState("");
  const mode = request.mode.toLowerCase();
  const expectsInput = mode === "input";

  React.useEffect(() => {
    setResponseText("");
  }, [request.requestId]);

  return (
    <div className="pointer-events-auto max-h-[45vh] overflow-auto agw-scrollbar rounded-md border bg-background/95 p-3 shadow-sm backdrop-blur">
      <div className="flex items-start gap-3">
        <div className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-md border bg-muted">
          <ShieldCheck className="h-4 w-4 text-primary" />
        </div>
        <div className="min-w-0 flex-1 space-y-1">
          <div className="flex min-w-0 items-center gap-2">
            <div className="truncate text-sm font-medium">{request.nodeName || "HumanGate"}</div>
            <Badge variant="secondary" className="h-5 rounded-md px-1.5 text-[11px]">
              {request.mode}
            </Badge>
          </div>
          <div className="text-sm text-foreground">{request.prompt}</div>
          {request.inputPreview ? (
            <div className="line-clamp-2 rounded-md bg-muted/60 px-2 py-1.5 text-xs text-muted-foreground">
              {request.inputPreview}
            </div>
          ) : null}
        </div>
      </div>

      {expectsInput ? (
        <Textarea
          value={responseText}
          onChange={(event) => setResponseText(event.target.value)}
          className="mt-3 min-h-18 resize-none text-sm"
          placeholder="Response"
        />
      ) : null}

      <div className="mt-3 flex justify-end gap-2">
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => onReject(responseText.trim() || undefined)}
        >
          <X className="h-4 w-4" />
          Reject
        </Button>
        <Button type="button" size="sm" onClick={() => onApprove(responseText.trim() || undefined)}>
          <Check className="h-4 w-4" />
          Approve
        </Button>
      </div>
    </div>
  );
}
