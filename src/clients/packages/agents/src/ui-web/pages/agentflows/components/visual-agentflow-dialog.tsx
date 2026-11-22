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
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
  Input,
} from "@agw/components";
import { Check, CircleAlert, LoaderCircle, MoreHorizontal, Redo2, Undo2, X } from "lucide-react";
import { useShallow } from "zustand/react/shallow";

import type {
  AgentDto,
  AgentflowDetailResponse,
  AgentflowDto,
  ModelProviderDto,
} from "../../../../types/agentflow";
import { createEditorDocument, createEmptyDocument } from "./agentflow-document";
import {
  AgentflowEditorProvider,
  getAgentflowDocumentFingerprint,
  selectCanRedo,
  selectCanUndo,
  useAgentflowEditorStore,
  useAgentflowEditorStoreApi,
  type AgentflowEditorState,
} from "./agentflow-editor-store";
import { isEditableKeyboardTarget } from "./agentflow-keyboard";
import { hasPassedValidation, type AgentflowProblemsState } from "./agentflow-problems";
import { useAgentflowRevisionActions } from "./agentflow-revision-actions";
import { VisualAgentflowBuilder, type AgentflowBuilderActions } from "./visual-agentflow-builder";

type VisualAgentflowDialogProps = {
  open: boolean;
  onOpenChange(open: boolean): void;
  agents: AgentDto[];
  agentflows: AgentflowDto[];
  modelProviders: ModelProviderDto[];
  editingAgentflow?: AgentflowDetailResponse | null;
  onAgentflowSaved?(): void;
};

type RevisionActions = ReturnType<typeof useAgentflowRevisionActions>;
type BuilderActionsRef = React.RefObject<AgentflowBuilderActions | null>;
type RevisionButtonState = Pick<
  AgentflowEditorState,
  | "agentflowId"
  | "baseRevision"
  | "publishedRevision"
  | "document"
  | "problems"
  | "isSaving"
  | "isDirty"
>;

export function VisualAgentflowDialog(props: VisualAgentflowDialogProps) {
  if (!props.open) return null;
  const detail = props.editingAgentflow;
  const initial = detail
    ? {
        document: createEditorDocument(detail.agentflow, detail.definition),
        agentflowId: detail.agentflow.id,
        baseRevision: detail.revision,
        publishedRevision: detail.agentflow.publishedRevision,
      }
    : { document: createEmptyDocument(), agentflowId: null, baseRevision: 0, publishedRevision: 0 };
  return (
    <AgentflowEditorProvider initial={initial}>
      <VisualAgentflowDialogSession {...props} />
    </AgentflowEditorProvider>
  );
}

function VisualAgentflowDialogSession(props: VisualAgentflowDialogProps) {
  const actions = useAgentflowRevisionActions(props.onAgentflowSaved);
  const builderActions = React.useRef<AgentflowBuilderActions | null>(null);
  const requestValidation = useAgentflowEditorStore((state) => state.requestValidation);
  const close = () => props.onOpenChange(false);
  const { discardOpen, setDiscardOpen, requestClose } = useEditorClose(close);
  useEditorHistoryShortcuts();
  return (
    <>
      <EditorDialogFrame requestClose={requestClose}>
        <EditorHeader
          editing={Boolean(props.editingAgentflow)}
          actions={actions}
          builderActions={builderActions}
          requestClose={requestClose}
        />
        <div className="min-h-0 flex-1">
          <VisualAgentflowBuilder
            actionsRef={builderActions}
            agents={props.agents}
            agentflows={props.agentflows}
            modelProviders={props.modelProviders}
            revisions={actions.revisions}
            onSave={() => void actions.save()}
            onRetryValidation={requestValidation}
            onOpenRevision={(revision) => void actions.open(revision)}
            onPublishRevision={(revision) => void actions.publish(revision)}
          />
        </div>
      </EditorDialogFrame>
      <DiscardDialog open={discardOpen} onOpenChange={setDiscardOpen} onDiscard={close} />
      <RevisionConflictDialog actions={actions} />
    </>
  );
}

function EditorDialogFrame({
  children,
  requestClose,
}: React.PropsWithChildren<{ requestClose(): void }>) {
  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) requestClose();
      }}
    >
      <DialogContent
        size="fullscreen"
        className="gap-0 p-0"
        onInteractOutside={(event) => event.preventDefault()}
        onPointerDownOutside={(event) => event.preventDefault()}
        onEscapeKeyDown={(event) => event.preventDefault()}
        showCloseButton={false}
      >
        {children}
      </DialogContent>
    </Dialog>
  );
}

function useEditorClose(close: () => void) {
  const store = useAgentflowEditorStoreApi();
  const [discardOpen, setDiscardOpen] = React.useState(false);
  return {
    discardOpen,
    setDiscardOpen,
    requestClose: () => {
      const state = store.getState();
      if (state.isSaving) return;
      state.commitHistoryGroup();
      if (state.isDirty) setDiscardOpen(true);
      else close();
    },
  };
}

function useEditorHistoryShortcuts() {
  const store = useAgentflowEditorStoreApi();
  React.useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if (isEditableKeyboardTarget(event.target) || event.altKey) return;
      const state = store.getState();
      if (state.isSaving) return;
      const key = event.key.toLowerCase();
      const command = event.metaKey || event.ctrlKey;
      const undo = command && key === "z" && !event.shiftKey;
      const redo = (command && key === "z" && event.shiftKey) || (event.ctrlKey && key === "y");
      if (undo && selectCanUndo(state)) {
        event.preventDefault();
        state.undo();
      } else if (redo && selectCanRedo(state)) {
        event.preventDefault();
        state.redo();
      }
    };
    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [store]);
}

function EditorHeader({
  editing,
  actions,
  builderActions,
  requestClose,
}: {
  editing: boolean;
  actions: RevisionActions;
  builderActions: BuilderActionsRef;
  requestClose(): void;
}) {
  const isSaving = useAgentflowEditorStore((state) => state.isSaving);
  return (
    <DialogHeader data-window-drag="true" className="shrink-0 border-b px-4 py-2">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <DialogTitle className="sr-only">
          {editing ? "Edit Agentflow" : "Create Agentflow"}
        </DialogTitle>
        <EditorName disabled={isSaving} />
        <RevisionLabels />
        <div className="ml-auto flex flex-wrap items-center gap-1">
          <ProblemsButton />
          <HistoryButtons disabled={isSaving} />
          <EditorMenu actions={builderActions} />
          <RevisionButtons actions={actions} />
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            aria-label="Close editor"
            title="Close editor"
            disabled={isSaving}
            onClick={requestClose}
          >
            <X className="h-4 w-4" />
          </Button>
        </div>
      </div>
      <DialogDescription className="sr-only">
        Edit Agentflow definitions, validate changes, publish revisions and run them.
      </DialogDescription>
    </DialogHeader>
  );
}

function EditorName({ disabled }: { disabled: boolean }) {
  const name = useAgentflowEditorStore((state) => state.document.name);
  const updateDocument = useAgentflowEditorStore((state) => state.updateDocument);
  const commitHistoryGroup = useAgentflowEditorStore((state) => state.commitHistoryGroup);
  return (
    <Input
      aria-label="Agentflow name"
      value={name}
      onChange={(event) =>
        updateDocument((document) => ({ ...document, name: event.target.value }), { group: "name" })
      }
      onBlur={commitHistoryGroup}
      placeholder="Untitled Agentflow"
      disabled={disabled}
      className="w-full max-w-xs border-transparent bg-transparent text-sm font-semibold shadow-none focus-visible:border-input"
    />
  );
}

function RevisionLabels() {
  const { baseRevision, publishedRevision, isDirty } = useAgentflowEditorStore(
    useShallow((state) => ({
      baseRevision: state.baseRevision,
      publishedRevision: state.publishedRevision,
      isDirty: state.isDirty,
    })),
  );
  return (
    <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-muted-foreground">
      {baseRevision > 0 ? <span>v{baseRevision}</span> : null}
      <span>{publishedRevision > 0 ? `Published v${publishedRevision}` : "Not published"}</span>
      {isDirty ? (
        <span className="inline-flex items-center gap-1.5 text-amber-700 dark:text-amber-400">
          <span className="h-1.5 w-1.5 rounded-full bg-current" aria-hidden="true" />
          Unsaved changes
        </span>
      ) : null}
    </div>
  );
}

function ProblemsButton() {
  const { status, problems } = useAgentflowEditorStore((state) => state.problems);
  const showPanel = useAgentflowEditorStore((state) => state.showPanel);
  const iconClass = "mr-1 h-4 w-4";
  const checking = status === "checking";
  const failed = status === "failed";
  const label = checking
    ? "Checking…"
    : failed
      ? "Validation failed"
      : problems.length
        ? `${problems.length} problems`
        : "No problems";
  return (
    <Button
      type="button"
      variant="ghost"
      size="sm"
      onClick={() => showPanel("problems")}
      title="Agentflow problems"
    >
      {checking ? (
        <LoaderCircle className={`${iconClass} animate-spin`} />
      ) : failed || problems.length ? (
        <CircleAlert className={`${iconClass} text-destructive`} />
      ) : (
        <Check className={iconClass} />
      )}
      {label}
    </Button>
  );
}

function HistoryButtons({ disabled }: { disabled: boolean }) {
  const canUndo = useAgentflowEditorStore(selectCanUndo);
  const canRedo = useAgentflowEditorStore(selectCanRedo);
  const undo = useAgentflowEditorStore((state) => state.undo);
  const redo = useAgentflowEditorStore((state) => state.redo);
  return (
    <>
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        aria-label="Undo"
        title="Undo"
        disabled={disabled || !canUndo}
        onClick={undo}
      >
        <Undo2 className="h-4 w-4" />
      </Button>
      <Button
        type="button"
        variant="ghost"
        size="icon-sm"
        aria-label="Redo"
        title="Redo"
        disabled={disabled || !canRedo}
        onClick={redo}
      >
        <Redo2 className="h-4 w-4" />
      </Button>
    </>
  );
}

function EditorMenu({ actions }: { actions: BuilderActionsRef }) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          type="button"
          variant="ghost"
          size="icon-sm"
          aria-label="More options"
          title="More options"
        >
          <MoreHorizontal className="h-4 w-4" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start">
        <DropdownMenuItem onSelect={() => actions.current?.openTemplates()}>
          Templates
        </DropdownMenuItem>
        <DropdownMenuItem onSelect={() => actions.current?.beginImport()}>Import</DropdownMenuItem>
        <DropdownMenuItem onSelect={() => actions.current?.beginExport()}>Export</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

function RevisionButtons({ actions }: { actions: RevisionActions }) {
  const state = useAgentflowEditorStore(
    useShallow((current) => ({
      agentflowId: current.agentflowId,
      baseRevision: current.baseRevision,
      publishedRevision: current.publishedRevision,
      document: current.document,
      problems: current.problems,
      isSaving: current.isSaving,
      isDirty: current.isDirty,
    })),
  );
  const buttons = getRevisionButtons(state);
  return (
    <>
      <RevisionButton {...buttons.run} onClick={() => void actions.run()}>
        Run
      </RevisionButton>
      <RevisionButton {...buttons.publish} onClick={() => void actions.publish(state.baseRevision)}>
        Publish
      </RevisionButton>
      <RevisionButton {...buttons.save} variant="default" onClick={() => void actions.save()}>
        {state.isSaving ? "Saving…" : "Save"}
      </RevisionButton>
    </>
  );
}

function RevisionButton(props: React.ComponentProps<typeof Button>) {
  return <Button type="button" size="sm" variant="outline" {...props} />;
}

function getRevisionButtons(state: RevisionButtonState) {
  const { agentflowId, baseRevision, publishedRevision, document, problems, isSaving, isDirty } =
    state;
  const fingerprint = getAgentflowDocumentFingerprint(document);
  const validationPassed = hasPassedValidation(problems, fingerprint);
  const waitingForSave = !agentflowId || isDirty;
  const alreadyPublished = publishedRevision > 0 && baseRevision === publishedRevision;
  return {
    run: {
      title: validationPassed ? "Run" : validationTitle(problems, fingerprint, "running"),
      disabled: !validationPassed || isSaving,
    },
    publish: {
      title: waitingForSave
        ? "Save before publishing"
        : alreadyPublished
          ? `v${baseRevision} is published`
          : validationPassed
            ? "Publish"
            : validationTitle(problems, fingerprint, "publishing"),
      disabled: waitingForSave || alreadyPublished || isSaving || !validationPassed,
    },
    save: {
      title: isSaving
        ? "Saving…"
        : !isDirty
          ? "No changes to save"
          : validationPassed
            ? "Save"
            : validationTitle(problems, fingerprint, "saving"),
      disabled: isSaving || !isDirty || !validationPassed,
    },
  };
}

function validationTitle(state: AgentflowProblemsState, fingerprint: string, action: string) {
  if (state.status === "failed") return `Retry validation before ${action}`;
  if (state.status === "checking" || state.fingerprint !== fingerprint)
    return `Wait for validation before ${action}`;
  return `Fix ${state.problems.length} problems before ${action}`;
}

function DiscardDialog({
  open,
  onOpenChange,
  onDiscard,
}: {
  open: boolean;
  onOpenChange(open: boolean): void;
  onDiscard(): void;
}) {
  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Discard unsaved changes?</AlertDialogTitle>
          <AlertDialogDescription>
            Your changes to this Agentflow will be lost. This action cannot be undone.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Keep editing</AlertDialogCancel>
          <AlertDialogAction variant="destructive" onClick={onDiscard}>
            Discard
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}

function RevisionConflictDialog({ actions }: { actions: RevisionActions }) {
  const setBaseRevision = useAgentflowEditorStore((state) => state.setBaseRevision);
  const revision = actions.conflictRevision;
  if (revision === null) return null;
  const reload = async () => {
    if (await actions.open(revision, true)) actions.closeConflict();
  };
  const saveAs = () => {
    setBaseRevision(revision);
    actions.closeConflict();
    void actions.save(revision);
  };
  return (
    <AlertDialog
      open
      onOpenChange={(open) => {
        if (!open) actions.closeConflict();
      }}
    >
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>This Agentflow was saved somewhere else.</AlertDialogTitle>
          <AlertDialogDescription>
            Load the latest revision or save this draft as a new revision.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Keep editing</AlertDialogCancel>
          <Button type="button" variant="outline" onClick={() => void reload()}>
            Load v{revision}
          </Button>
          <AlertDialogAction onClick={saveAs}>Save as v{revision + 1}</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
