"use client";

import { Check, ShieldCheck, X } from "lucide-react";

import type { ApprovalScope, PendingInteraction, PermissionMode } from "@agw/chat-runtime";
import { Button } from "@agw/components";
import { Badge } from "@agw/components";

type HumanGateApprovalProps = {
  request: Extract<PendingInteraction, { kind: "tool-approval" }>;
  permissionMode?: PermissionMode;
  onApprove: (scope: ApprovalScope) => void;
  onReject: () => void;
};

export function HumanGateApproval({
  request,
  permissionMode,
  onApprove,
  onReject,
}: HumanGateApprovalProps) {
  if (permissionMode === "fullAccess") {
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
              {request.source.toolName || "Tool approval"}
            </div>
            <Badge variant="secondary" className="min-h-5 rounded-md px-1.5 text-11 leading-none">
              {request.kind}
            </Badge>
          </div>
          <div className="whitespace-pre-wrap break-words text-sm text-foreground">
            {request.prompt}
          </div>
          {request.arguments != null ? (
            <pre className="mt-2 whitespace-pre-wrap break-words rounded-md border bg-muted/40 p-2 font-mono text-sm leading-relaxed text-muted-foreground">
              {JSON.stringify(request.arguments, null, 2)}
            </pre>
          ) : null}
        </div>
      </div>

      <div className="mt-3 flex justify-end gap-2">
        <Button type="button" variant="outline" size="sm" onClick={() => onReject()}>
          <X className="h-4 w-4" />
          Reject
        </Button>
        {permissionMode === "alwaysAsk" ? (
          <Button type="button" size="sm" onClick={() => onApprove("Once")}>
            <Check className="h-4 w-4" />
            Allow once
          </Button>
        ) : permissionMode === "allowSameArguments" ? (
          <Button type="button" size="sm" onClick={() => onApprove("AlwaysArguments")}>
            <Check className="h-4 w-4" />
            Allow same arguments
          </Button>
        ) : (
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
        )}
      </div>
    </div>
  );
}
