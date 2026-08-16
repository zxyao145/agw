"use client";

import * as React from "react";
import { Check, ShieldCheck, X } from "lucide-react";

import type { PendingHumanGate } from "../../../services/execution-hub";
import type { PermissionMode } from "../../../services/execution-hub";
import { Button } from "@agw/components";
import { Badge } from "@agw/components";
import { Textarea } from "@agw/components";
import { HumanInteractionPanel } from "./human-interaction-panel";

type HumanGateApprovalProps = {
  request: PendingHumanGate;
  permissionMode?: PermissionMode;
  onApprove: (
    approvalScope: "once" | "always-tool" | "always-arguments",
    responseText?: string,
    responseData?: unknown,
  ) => void;
  onReject: (responseText?: string) => void;
};

export function HumanGateApproval({
  request,
  permissionMode,
  onApprove,
  onReject,
}: HumanGateApprovalProps) {
  const [responseText, setResponseText] = React.useState("");
  const mode = request.mode.toLowerCase();
  const expectsInput = mode === "input";
  const isToolApproval = mode === "tool-approval";
  const rejectLabel = expectsInput ? "Interrupt" : "Reject";
  const approveLabel = expectsInput ? "Submit" : "Approve";

  React.useEffect(() => {
    setResponseText("");
  }, [request.requestId]);

  if (request.requestType === "human-interaction") {
    return (
      <HumanInteractionPanel
        request={{ ...request, requestType: "human-interaction" }}
        onSubmit={(responseData) => onApprove("once", undefined, responseData)}
        onCancel={() => onReject()}
      />
    );
  }

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
                ? request.toolName || "Tool approval"
                : request.nodeName || "HumanGate"}
            </div>
            <Badge variant="secondary" className="h-5 rounded-md px-1.5 text-[11px]">
              {request.mode}
            </Badge>
          </div>
          <div className="whitespace-pre-wrap break-words text-sm text-foreground">
            {request.prompt}
          </div>
          {request.inputPreview ? (
            <div className="whitespace-pre-wrap break-words rounded-md bg-muted/60 px-2 py-1.5 text-xs text-muted-foreground">
              {request.inputPreview}
            </div>
          ) : null}
          {isToolApproval && request.arguments ? (
            <pre className="mt-2 whitespace-pre-wrap break-words rounded-md border bg-muted/40 p-2 font-mono text-[11px] leading-relaxed text-muted-foreground">
              {request.arguments}
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
          <Button type="button" size="sm" onClick={() => onApprove("once")}>
            <Check className="h-4 w-4" />
            Allow once
          </Button>
        ) : isToolApproval && permissionMode === "allowSameArguments" ? (
          <Button type="button" size="sm" onClick={() => onApprove("always-arguments")}>
            <Check className="h-4 w-4" />
            Allow same arguments
          </Button>
        ) : isToolApproval ? (
          <>
            <Button type="button" variant="outline" size="sm" onClick={() => onApprove("once")}>
              <Check className="h-4 w-4" />
              Allow once
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => onApprove("always-arguments")}
            >
              Allow same arguments
            </Button>
            <Button type="button" size="sm" onClick={() => onApprove("always-tool")}>
              <ShieldCheck className="h-4 w-4" />
              Always allow tool
            </Button>
          </>
        ) : (
          <Button
            type="button"
            size="sm"
            onClick={() => onApprove("once", responseText.trim() || undefined)}
          >
            <Check className="h-4 w-4" />
            {approveLabel}
          </Button>
        )}
      </div>
    </div>
  );
}
