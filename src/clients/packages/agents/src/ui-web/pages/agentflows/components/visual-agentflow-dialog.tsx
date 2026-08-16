"use client";

import * as React from "react";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@agw/components";

import {
  type AgentflowBuilderActionState,
  createAgentflowEditorDocument,
  VisualAgentflowBuilder,
} from "./visual-agentflow-builder";
import {
  AgentflowEditorProvider,
  selectCanRedo,
  selectCanUndo,
  useAgentflowEditorStore,
} from "./agentflow-editor-store";
import { Redo2, Undo2, X } from "lucide-react";
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

export function VisualAgentflowDialog(props: VisualAgentflowDialogProps) {
  if (!props.open) return null;

  return (
    <AgentflowEditorProvider
      initialDocument={createAgentflowEditorDocument({
        editingAgentflow: props.editingAgentflow,
        agents: props.agents,
        agentflows: props.agentflows,
      })}
    >
      <VisualAgentflowDialogSession {...props} />
    </AgentflowEditorProvider>
  );
}

function VisualAgentflowDialogSession({
  onOpenChange,
  agents,
  agentflows,
  modelProviders,
  editingAgentflow,
  onAgentflowCreated,
}: VisualAgentflowDialogProps) {
  const [builderActionState, setBuilderActionState] =
    React.useState<AgentflowBuilderActionState | null>(null);
  const [discardConfirmationOpen, setDiscardConfirmationOpen] = React.useState(false);
  const isDirty = useAgentflowEditorStore((state) => state.isDirty);
  const isSaving = useAgentflowEditorStore((state) => state.isSaving);
  const canUndo = useAgentflowEditorStore(selectCanUndo);
  const canRedo = useAgentflowEditorStore(selectCanRedo);
  const commitHistoryGroup = useAgentflowEditorStore((state) => state.commitHistoryGroup);
  const undo = useAgentflowEditorStore((state) => state.undo);
  const redo = useAgentflowEditorStore((state) => state.redo);

  const handleAgentflowCreated = React.useCallback(() => {
    onAgentflowCreated?.();
    onOpenChange(false);
  }, [onAgentflowCreated, onOpenChange]);

  const requestClose = React.useCallback(() => {
    if (isSaving) return;
    commitHistoryGroup();
    if (isDirty) {
      setDiscardConfirmationOpen(true);
      return;
    }

    onOpenChange(false);
  }, [commitHistoryGroup, isDirty, isSaving, onOpenChange]);

  React.useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (isEditableKeyboardTarget(event.target) || event.altKey) return;

      const key = event.key.toLowerCase();
      const commandKey = event.metaKey || event.ctrlKey;
      const wantsUndo = commandKey && key === "z" && !event.shiftKey;
      const wantsRedo =
        (commandKey && key === "z" && event.shiftKey) ||
        (event.ctrlKey && !event.metaKey && key === "y");

      if (wantsUndo && canUndo && !isSaving) {
        event.preventDefault();
        undo();
      } else if (wantsRedo && canRedo && !isSaving) {
        event.preventDefault();
        redo();
      }
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [canRedo, canUndo, isSaving, redo, undo]);

  return (
    <>
      <Dialog
        open
        onOpenChange={(nextOpen) => {
          if (!nextOpen) requestClose();
        }}
      >
        <DialogContent
          size="fullscreen"
          className="gap-0 p-0"
          onInteractOutside={(event) => event.preventDefault()}
          onPointerDownOutside={(event) => event.preventDefault()}
          showCloseButton={false}
        >
          <DialogHeader className="shrink-0 border-b px-6 py-2">
            <div className="flex items-center justify-between gap-4">
              <div className="min-w-0">
                <DialogTitle>
                  {editingAgentflow ? "Edit Agentflow" : "Visual Agentflow Builder"}
                </DialogTitle>
                <DialogDescription className="truncate">
                  {editingAgentflow
                    ? `Editing agentflow: ${editingAgentflow.name}`
                    : "Design a workflow graph with agents, workflow-as-agent nodes, orchestration blocks, human gates, checkpoints, branches, and controlled loops."}
                </DialogDescription>
              </div>
              <div className="flex shrink-0 items-center gap-2">
                {isDirty ? (
                  <span className="mr-1 inline-flex items-center gap-1.5 text-xs text-amber-700 dark:text-amber-400">
                    <span className="h-1.5 w-1.5 rounded-full bg-current" aria-hidden="true" />
                    Unsaved changes
                  </span>
                ) : null}
                <Button
                  type="button"
                  variant="outline"
                  size="icon-sm"
                  title="Undo (Cmd/Ctrl+Z)"
                  aria-label="Undo"
                  disabled={!canUndo || isSaving}
                  onClick={undo}
                >
                  <Undo2 className="h-4 w-4" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="icon-sm"
                  title="Redo (Cmd/Ctrl+Shift+Z or Ctrl+Y)"
                  aria-label="Redo"
                  disabled={!canRedo || isSaving}
                  onClick={redo}
                >
                  <Redo2 className="h-4 w-4" />
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  className="cursor-pointer"
                  disabled={isSaving}
                  onClick={requestClose}
                >
                  Cancel
                </Button>
                <Button
                  type="button"
                  size="sm"
                  className="cursor-pointer"
                  disabled={!builderActionState || builderActionState.disabled}
                  onClick={() => builderActionState?.submit()}
                >
                  {builderActionState?.label ?? (editingAgentflow ? "Update" : "Create")}
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-sm"
                  title="Close editor"
                  aria-label="Close editor"
                  disabled={isSaving}
                  onClick={requestClose}
                >
                  <X className="h-4 w-4" />
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

      <AlertDialog open={discardConfirmationOpen} onOpenChange={setDiscardConfirmationOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Discard unsaved changes?</AlertDialogTitle>
            <AlertDialogDescription>
              Your changes to this Agentflow will be lost. This action cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Keep editing</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={() => onOpenChange(false)}>
              Discard
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}

function isEditableKeyboardTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return (
    target.isContentEditable ||
    Boolean(target.closest("input, textarea, select, [contenteditable]"))
  );
}
