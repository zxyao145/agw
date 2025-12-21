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
  DialogTrigger,
} from "@/components/ui/dialog"
import { VisualWorkflowBuilder } from "./visual-workflow-builder"
import { X } from "lucide-react"

type AgentDto = {
  id: string
  name: string
  instructions: string
  systemPrompt: string
  modelProviderApiKeyId: string
  createBy?: string | null
  createTime?: string | null
  updateBy?: string | null
  updateTime?: string | null
}

type VisualWorkflowDialogProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  agents: AgentDto[]
  onBuild: (visualData: {
    agents: { agentId: string; order: number; role: string | null }[]
    pattern: number
  }) => void
}

export function VisualWorkflowDialog({
  open,
  onOpenChange,
  agents,
  onBuild,
}: VisualWorkflowDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogTrigger asChild>
        <Button variant="outline">Visual Builder</Button>
      </DialogTrigger>
      <DialogContent
        className="fixed inset-0 w-screen h-screen max-w-none sm:max-w-none max-h-none m-0 p-6 flex flex-col translate-x-0 translate-y-0 rounded-none border-0"
        onInteractOutside={(e) => e.preventDefault()}
        onPointerDownOutside={(e) => e.preventDefault()}
        showCloseButton={false}
      >
        <DialogHeader className="shrink-0">
          <div className="flex items-center justify-between">
            <div>
              <UiDialogTitle>Visual Workflow Builder</UiDialogTitle>
              <UiDialogDescription>
                Drag agents onto the canvas and connect them to build your workflow visually.
              </UiDialogDescription>
            </div>
            <DialogClose asChild>
              <Button variant="outline" size="sm" className="cursor-pointer">
                <X />
              </Button>
            </DialogClose>
          </div>
        </DialogHeader>
        <div className="flex-1 min-h-0 mt-4">
          <VisualWorkflowBuilder agents={agents} onBuild={onBuild} />
        </div>
      </DialogContent>
    </Dialog>
  )
}
