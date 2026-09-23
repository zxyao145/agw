"use client";

import * as React from "react";
import { Check, ShieldCheck, X } from "lucide-react";

import type { ApprovalScope, PendingInteraction, PermissionMode } from "@agw/chat-runtime";
import { Button } from "@agw/components";
import { Badge } from "@agw/components";
import { Textarea } from "@agw/components";

type HumanGateApprovalProps = {
  request: Exclude<PendingInteraction, { kind: "user-input" }>;
  permissionMode?: PermissionMode;
  onApprove: (scope: ApprovalScope, responseText?: string) => void;
  onReject: (responseText?: string) => void;
};

export function HumanGateApproval({
  request,
  permissionMode,
  onApprove,
  onReject,
}: HumanGateApprovalProps) {
  const [responseText, setResponseText] = React.useState("");
  const mode = request.kind === "workflow-gate" ? request.mode.toLowerCase() : "tool-approval";
  const expectsInput = mode === "input";
  const isToolApproval = request.kind === "tool-approval";
  const rejectLabel = expectsInput ? "Interrupt" : "Reject";
  const approveLabel = expectsInput ? "Submit" : "Approve";

  React.useEffect(() => {
    setResponseText("");
  }, [request.interactionId]);

  if (isToolApproval && permissionMode === "fullAccess") {
    return null;
  }

  return (
    <div className="pointer-events-auto rounded-md border bg-background/95 p-3 shadow-sm backdrop-blur">
      <div className="flex items-start gap-3">
        <div className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-md border bg-muted">
          <ShieldCheck className="h-4 w-4 text-primary" />
        </div>
        <div className="min-w-0 flex-1 space-y-1">
          <div className="flex min-w-0 items-center gap-2">
            <div className="truncate text-sm font-medium">
              {isToolApproval
                ? request.source.toolName || "Tool approval"
                : request.source.nodeName || "HumanGate"}
            </div>
            <Badge variant="secondary" className="h-5 rounded-md px-1.5 text-[11px]">
              {mode}
            </Badge>
          </div>
          <div className="whitespace-pre-wrap break-words text-sm text-foreground">
            {request.prompt}
          </div>
          {request.kind === "workflow-gate" && request.inputPreview ? (
            <div className="whitespace-pre-wrap break-words rounded-md bg-muted/60 px-2 py-1.5 text-xs text-muted-foreground">
              {request.inputPreview}
            </div>
          ) : null}
          {request.kind === "tool-approval" && request.arguments != null ? (
            <pre className="mt-2 whitespace-pre-wrap break-words rounded-md border bg-muted/40 p-2 font-mono text-[11px] leading-relaxed text-muted-foreground">
              {JSON.stringify(request.arguments, null, 2)}
            </pre>
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
          {rejectLabel}
        </Button>
        {isToolApproval && permissionMode === "alwaysAsk" ? (
          <Button type="button" size="sm" onClick={() => onApprove("Once")}>
            <Check className="h-4 w-4" />
            Allow once
          </Button>
        ) : isToolApproval && permissionMode === "allowSameArguments" ? (
          <Button type="button" size="sm" onClick={() => onApprove("AlwaysArguments")}>
            <Check className="h-4 w-4" />
            Allow same arguments
          </Button>
        ) : isToolApproval ? (
          <>
            <Button type="button" variant="outline" size="sm" onClick={() => onApprove("Once")}>
              <Check className="h-4 w-4" />
              Allow once
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => onApprove("AlwaysArguments")}
            >
              Allow same arguments
            </Button>
            <Button type="button" size="sm" onClick={() => onApprove("AlwaysTool")}>
              <ShieldCheck className="h-4 w-4" />
              Always allow tool
            </Button>
          </>
        ) : (
          <Button
            type="button"
            size="sm"
            onClick={() => onApprove("Once", responseText.trim() || undefined)}
          >
            <Check className="h-4 w-4" />
            {approveLabel}
          </Button>
        )}
      </div>
    </div>
  );
}
