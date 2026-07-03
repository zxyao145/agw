"use client";

import * as React from "react";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription as UiDialogDescription,
  DialogHeader,
  DialogTitle as UiDialogTitle,
} from "@/components/ui/dialog";
import {
  type AgentflowBuilderActionState,
  VisualAgentflowBuilder,
} from "./visual-agentflow-builder";
import { Waypoints, X } from "lucide-react";
import { AgentDto, AgentflowDetailDto, AgentflowDto } from "@/types/agentflow";

type VisualAgentflowDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  editingAgentflow?: AgentflowDetailDto | null;
  onAgentflowCreated?: () => void;
  onViewMermaid: (agentflow: AgentflowDto) => void;
};

export function VisualAgentflowDialog({
  open,
  onOpenChange,
  agents,
  agentflows,
  editingAgentflow,
  onAgentflowCreated,
  onViewMermaid,
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
        className="fixed inset-0 w-screen h-screen max-w-none sm:max-w-none max-h-none m-0 p-4 flex flex-col translate-x-0 translate-y-0 rounded-none border-0"
        onInteractOutside={(e) => e.preventDefault()}
        onPointerDownOutside={(e) => e.preventDefault()}
        showCloseButton={false}
      >
        <DialogHeader className="shrink-0">
          <div className="flex items-center justify-between">
            <div>
              <UiDialogTitle>
                {editingAgentflow ? "Edit Agentflow" : "Visual Agentflow Builder"}
              </UiDialogTitle>
              <UiDialogDescription>
                {editingAgentflow
                  ? `Editing agentflow: ${editingAgentflow.name}`
                  : "Design a DAG by adding agents, workflow-as-agent nodes, orchestration blocks, human gates, checkpoints, and MAF-aligned edges."}
              </UiDialogDescription>
            </div>
            <div className="flex items-center gap-2">
              {editingAgentflow ? (
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="cursor-pointer"
                  onClick={() => onViewMermaid(editingAgentflow)}
                  title="View Mermaid chart"
                >
                  <Waypoints />
                  Visualization
                </Button>
              ) : null}
              <Button
                type="button"
                size="sm"
                className="cursor-pointer"
                disabled={!builderActionState || builderActionState.disabled}
                onClick={() => builderActionState?.submit()}
              >
                {builderActionState?.label ?? (editingAgentflow ? "Update" : "Create")}
              </Button>
              <DialogClose asChild>
                <Button variant="outline" size="sm" className="cursor-pointer">
                  <X />
                </Button>
              </DialogClose>
            </div>
          </div>
        </DialogHeader>
        <div className="flex-1 min-h-0">
          <VisualAgentflowBuilder
            agents={agents}
            agentflows={agentflows}
            editingAgentflow={editingAgentflow}
            onAgentflowCreated={handleAgentflowCreated}
            onActionStateChange={setBuilderActionState}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}
