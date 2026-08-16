"use client";

import { Flag, RotateCcw } from "lucide-react";

import { Badge, Button } from "@agw/components";

type AgentflowCheckpointCardProps = {
  name: string;
  showResume: boolean;
  available: boolean;
  disabled: boolean;
  onResume: () => void;
};

export function AgentflowCheckpointCard({
  name,
  showResume,
  available,
  disabled,
  onResume,
}: AgentflowCheckpointCardProps) {
  return (
    <div className="mx-4 rounded-xl border bg-card px-4 py-3 shadow-xs">
      <div className="flex items-center gap-3">
        <div className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-muted">
          <Flag className="size-4" />
        </div>
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <span className="truncate text-sm font-medium">{name}</span>
            <Badge variant="secondary" className="text-[10px]">
              Checkpoint
            </Badge>
          </div>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Snapshot saved. The workflow continued automatically.
          </p>
        </div>
        {showResume ? (
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={disabled || !available}
            onClick={onResume}
            title={available ? "Resume from this checkpoint" : "This checkpoint is unavailable"}
          >
            <RotateCcw className="size-4" />
            Resume
          </Button>
        ) : null}
      </div>
    </div>
  );
}
