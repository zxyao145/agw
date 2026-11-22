"use client";

import * as React from "react";
import { Button, ResizablePanel } from "@agw/components";
import { Check, ChevronDown, ChevronUp, CircleAlert, Play, Plus } from "lucide-react";

import type { AgentflowProblem, AgentflowRevisionDto } from "../../../../types/agentflow";
import { useAgentflowEditorStore } from "./agentflow-editor-store";

type AgentflowBottomPanelProps = {
  problems: AgentflowProblem[];
  problemStatus: "idle" | "checking" | "failed";
  problemError: string | null;
  revisions: AgentflowRevisionDto[];
  agentflowId: string | null;
  publishedRevision: number;
  runContent: React.ReactNode;
  onShowProblem(problem: AgentflowProblem): void;
  onRetryValidation(): void;
  onSave(): void;
  onOpenRevision(revision: number): void;
  onPublishRevision(revision: number): void;
  onNewRun(): void;
};

const COLLAPSED_PANEL_SIZE = 36;
const EXPANDED_PANEL_SIZE = 296;

type ResizablePanelProps = React.ComponentProps<typeof ResizablePanel>;
type ResizablePanelHandle =
  NonNullable<ResizablePanelProps["panelRef"]> extends React.Ref<infer Type> ? Type : never;
type WorkspacePanelProps = Omit<
  AgentflowBottomPanelProps,
  "agentflowId" | "problems" | "problemStatus" | "problemError" | "publishedRevision"
>;

export function AgentflowWorkspaceBottomPanel(props: WorkspacePanelProps) {
  const { panelRef, onResize, defaultSize } = useBottomPanelSize();
  const problems = useAgentflowEditorStore((state) => state.problems);
  const agentflowId = useAgentflowEditorStore((state) => state.agentflowId);
  const publishedRevision = useAgentflowEditorStore((state) => state.publishedRevision);
  return (
    <ResizablePanel
      id="agentflow-bottom-panel"
      panelRef={panelRef}
      defaultSize={defaultSize}
      minSize={COLLAPSED_PANEL_SIZE}
      maxSize="70%"
      onResize={onResize}
    >
      <AgentflowBottomPanel
        {...props}
        problems={problems.problems}
        problemStatus={problems.status}
        problemError={problems.error}
        agentflowId={agentflowId}
        publishedRevision={publishedRevision}
      />
    </ResizablePanel>
  );
}

function useBottomPanelSize() {
  const panelExpanded = useAgentflowEditorStore((state) => state.panelExpanded);
  const setPanelExpanded = useAgentflowEditorStore((state) => state.setPanelExpanded);
  const panelRef = React.useRef<ResizablePanelHandle>(null);
  const previousExpanded = React.useRef(panelExpanded);
  const changedByDrag = React.useRef(false);
  const syncing = React.useRef(false);
  const defaultSize = panelExpanded ? EXPANDED_PANEL_SIZE : COLLAPSED_PANEL_SIZE;

  React.useEffect(() => {
    const panel = panelRef.current;
    if (!panel || previousExpanded.current === panelExpanded) return;
    previousExpanded.current = panelExpanded;
    if (changedByDrag.current) {
      changedByDrag.current = false;
      return;
    }
    if (Math.abs(panel.getSize().inPixels - defaultSize) <= 1) return;
    syncing.current = true;
    panel.resize(defaultSize);
    syncing.current = false;
  }, [panelExpanded, defaultSize]);

  const onResize: NonNullable<ResizablePanelProps["onResize"]> = (size, _id, previousSize) => {
    if (!previousSize || syncing.current || previousExpanded.current !== panelExpanded) return;
    const currentSize = panelRef.current?.getSize().inPixels;
    if (currentSize !== undefined && Math.abs(currentSize - size.inPixels) > 1) return;
    const expanded = size.inPixels > 40;
    if (expanded !== panelExpanded) {
      changedByDrag.current = true;
      setPanelExpanded(expanded);
    }
  };
  return { panelRef, onResize, defaultSize };
}

export function AgentflowBottomPanel(props: AgentflowBottomPanelProps) {
  const panelTab = useAgentflowEditorStore((state) => state.panelTab);
  const panelExpanded = useAgentflowEditorStore((state) => state.panelExpanded);
  const showPanel = useAgentflowEditorStore((state) => state.showPanel);
  const setPanelExpanded = useAgentflowEditorStore((state) => state.setPanelExpanded);

  return (
    <section className="flex h-full min-h-9 flex-col border-t bg-background">
      <div className="flex h-9 shrink-0 items-center gap-1 px-2">
        <PanelTab active={panelTab === "problems"} onClick={() => showPanel("problems")}>
          {props.problemStatus === "checking"
            ? "Checking…"
            : `Problems${props.problems.length ? ` (${props.problems.length})` : ""}`}
        </PanelTab>
        <PanelTab active={panelTab === "run"} onClick={() => showPanel("run")}>
          Run
        </PanelTab>
        <PanelTab active={panelTab === "versions"} onClick={() => showPanel("versions")}>
          Versions
        </PanelTab>
        {panelTab === "run" ? (
          <Button
            variant="ghost"
            size="sm"
            className="ml-auto h-7"
            onClick={props.onNewRun}
            disabled={!props.agentflowId}
          >
            <Plus className="mr-1 h-3.5 w-3.5" /> New run
          </Button>
        ) : null}
        <Button
          type="button"
          size="icon-sm"
          variant="ghost"
          className={panelTab === "run" ? "h-7 w-7" : "ml-auto h-7 w-7"}
          onClick={() => {
            const expanded = !panelExpanded;
            setPanelExpanded(expanded);
          }}
          aria-label={panelExpanded ? "Collapse bottom panel" : "Expand bottom panel"}
          title={panelExpanded ? "Collapse panel" : "Expand panel"}
        >
          {panelExpanded ? <ChevronDown className="h-4 w-4" /> : <ChevronUp className="h-4 w-4" />}
        </Button>
      </div>
      <div className={panelExpanded ? "min-h-0 flex-1 overflow-auto border-t p-3" : "hidden"}>
        <div className={panelTab === "problems" ? "space-y-2" : "hidden"}>
          {props.problemStatus === "failed" ? (
            <div className="flex items-center justify-between gap-3 text-sm text-destructive">
              <span>{props.problemError}</span>
              <Button variant="outline" size="sm" onClick={props.onRetryValidation}>
                Retry
              </Button>
            </div>
          ) : props.problems.length ? (
            props.problems.map((problem, index) => (
              <div
                key={`${problem.code}-${problem.nodeId}-${problem.edgeId}-${index}`}
                className="flex items-center gap-2 text-sm"
              >
                <CircleAlert className="h-4 w-4 shrink-0 text-destructive" />
                <span className="min-w-0 flex-1">{problem.message}</span>
                <Button variant="ghost" size="sm" onClick={() => props.onShowProblem(problem)}>
                  Show on canvas
                </Button>
              </div>
            ))
          ) : props.problemStatus === "checking" ? (
            <p className="text-sm text-muted-foreground">Checking Agentflow…</p>
          ) : (
            <p className="flex items-center gap-2 text-sm text-muted-foreground">
              <Check className="h-4 w-4" /> No problems. This Agentflow can be saved and run.
            </p>
          )}
        </div>
        <div className={panelTab === "run" ? "h-full" : "hidden"}>
          {props.agentflowId ? (
            props.runContent
          ) : (
            <div className="flex h-full flex-col items-center justify-center gap-2 text-sm text-muted-foreground">
              <span>Save the Agentflow to run it.</span>
              <Button size="sm" onClick={props.onSave}>
                <Play className="mr-1 h-3.5 w-3.5" /> Save
              </Button>
            </div>
          )}
        </div>
        <div className={panelTab === "versions" ? "space-y-2" : "hidden"}>
          {props.agentflowId ? (
            props.revisions.map((item) => (
              <div key={item.revision} className="flex items-center gap-3 text-sm">
                <span className="font-medium">v{item.revision}</span>
                <span className="flex-1 text-xs text-muted-foreground">{item.createTime}</span>
                {item.revision === props.publishedRevision ? (
                  <span className="text-xs text-muted-foreground">Published</span>
                ) : null}
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => props.onOpenRevision(item.revision)}
                >
                  Open v{item.revision}
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => props.onPublishRevision(item.revision)}
                  disabled={item.revision === props.publishedRevision}
                >
                  Publish
                </Button>
              </div>
            ))
          ) : (
            <p className="text-sm text-muted-foreground">Save the Agentflow to view versions.</p>
          )}
        </div>
      </div>
    </section>
  );
}

function PanelTab({
  active,
  children,
  onClick,
}: React.PropsWithChildren<{ active: boolean; onClick(): void }>) {
  return (
    <Button
      type="button"
      variant={active ? "secondary" : "ghost"}
      size="sm"
      className="h-7"
      onClick={onClick}
    >
      {children}
    </Button>
  );
}
