"use client"

import * as React from "react"
import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription as UiDialogDescription,
  DialogHeader,
  DialogTitle as UiDialogTitle,
} from "@/components/ui/dialog"
import { VisualAgentflowBuilder } from "./visual-agentflow-builder"
import { X } from "lucide-react"
import { AgentDto, AgentflowDetailDto, AgentflowDto } from "@/types/agentflow";

type VisualAgentflowDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  agents: AgentDto[]
  agentflows: AgentflowDto[]
  editingAgentflow?: AgentflowDetailDto | null
  onAgentflowCreated?: () => void
}

export function VisualAgentflowDialog({
  open,
  onOpenChange,
  agents,
  agentflows,
  editingAgentflow: editingAgentflow,
  onAgentflowCreated,
}: VisualAgentflowDialogProps) {
  const handleAgentflowCreated = React.useCallback(() => {
    onAgentflowCreated?.()
    onOpenChange(false)
  }, [onAgentflowCreated, onOpenChange])

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
                  : "Design your agentflow by adding agents/agentflows as nodes and connecting them. The system will auto-detect the orchestration pattern based on your graph structure."}
              </UiDialogDescription>
            </div>
            <DialogClose asChild>
              <Button variant="outline" size="sm" className="cursor-pointer">
                <X />
              </Button>
            </DialogClose>
          </div>
        </DialogHeader>
        <div className="flex-1 min-h-0">
          <VisualAgentflowBuilder
            agents={agents}
            agentflows={agentflows}
            editingAgentflow={editingAgentflow}
            onAgentflowCreated={handleAgentflowCreated}
          />
        </div>
      </DialogContent>
    </Dialog>
  )
}
