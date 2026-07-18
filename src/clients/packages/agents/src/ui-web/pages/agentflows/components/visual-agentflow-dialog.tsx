"use client";

import * as React from "react";
import {
  Button,
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@agw/components";

import {
  type AgentflowBuilderActionState,
  VisualAgentflowBuilder,
} from "./visual-agentflow-builder";
import { X } from "lucide-react";
import type {
  AgentDto,
  AgentflowDetailDto,
  AgentflowDto,
  ModelProviderDto,
} from "../../../../types/agentflow";

type VisualAgentflowDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  modelProviders: ModelProviderDto[];
  editingAgentflow?: AgentflowDetailDto | null;
  onAgentflowCreated?: () => void;
};

export function VisualAgentflowDialog({
  open,
  onOpenChange,
  agents,
  agentflows,
  modelProviders,
  editingAgentflow,
  onAgentflowCreated,
}: VisualAgentflowDialogProps) {
  const [builderActionState, setBuilderActionState] =
    React.useState<AgentflowBuilderActionState | null>(null);

  const handleAgentflowCreated = React.useCallback(() => {
    onAgentflowCreated?.();
    onOpenChange(false);
  }, [onAgentflowCreated, onOpenChange]);

  React.useEffect(() => {
    if (open) return;
    setBuilderActionState(null);
  }, [open]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        size="fullscreen"
        className="gap-0 p-0"
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        showCloseButton={false}
      >
        <DialogHeader className="shrink-0 border-b px-6 py-2">
          <div className="flex items-center justify-between">
            <div>
              <DialogTitle>
                {editingAgentflow ? "Edit Agentflow" : "Visual Agentflow Builder"}
              </DialogTitle>
              <DialogDescription>
                {editingAgentflow
                  ? `Editing agentflow: ${editingAgentflow.name}`
                  : "Design a DAG by adding agents, workflow-as-agent nodes, orchestration blocks, human gates, checkpoints, and MAF-aligned edges."}
              </DialogDescription>
            </div>
            <div className="flex items-center gap-2">
              <DialogClose asChild>
                <Button variant="outline" size="sm" className="cursor-pointer">
                  Cancel
                </Button>
              </DialogClose>

              <Button
                type="button"
                size="sm"
                className="cursor-pointer"
                disabled={!builderActionState || builderActionState.disabled}
                onClick={() => builderActionState?.submit()}
              >
                {builderActionState?.label ?? (editingAgentflow ? "Update" : "Create")}
              </Button>
            </div>
          </div>
        </DialogHeader>
        <div className="flex-1 min-h-0">
          <VisualAgentflowBuilder
            agents={agents}
            agentflows={agentflows}
            modelProviders={modelProviders}
            editingAgentflow={editingAgentflow}
            onAgentflowCreated={handleAgentflowCreated}
            onActionStateChange={setBuilderActionState}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
