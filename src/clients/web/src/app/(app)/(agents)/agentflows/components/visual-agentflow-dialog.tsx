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
import { X } from "lucide-react";
import { AgentDto, AgentflowDetailDto, AgentflowDto, ModelProviderDto } from "@/types/agentflow";

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
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
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
