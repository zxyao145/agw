"use client";

import * as React from "react";
import type { AgentflowProblem, AgentflowRevisionDto } from "../../../../types/agentflow";
import { AgentflowWorkspaceBottomPanel } from "./agentflow-bottom-panel";
import { useAgentflowEditorStore } from "./agentflow-editor-store";
import { AgentflowRunPanel } from "./agentflow-run-panel";
import { useAgentflowValidation } from "./use-agentflow-validation";

type AgentflowBuilderBottomPanelProps = {
  revisions?: AgentflowRevisionDto[];
  onSave?(): void;
  onOpenRevision?(revision: number): void;
  onPublishRevision?(revision: number): void;
  onRetryValidation?(): void;
  onShowNode(nodeId: string): void;
  onShowEdge(edgeId: string): void;
};

export function AgentflowBuilderBottomPanel(props: AgentflowBuilderBottomPanelProps) {
  const {
    revisions = [],
    onSave,
    onOpenRevision,
    onPublishRevision,
    onRetryValidation,
    onShowNode,
    onShowEdge,
  } = props;
  const [runSeed, setRunSeed] = React.useState(0);
  const resetRunState = useAgentflowEditorStore((state) => state.resetRunState);
  const selectEdge = useAgentflowEditorStore((state) => state.selectEdge);
  const requestValidation = useAgentflowValidation();

  const showProblem = (problem: AgentflowProblem) => {
    if (problem.nodeId) onShowNode(problem.nodeId);
    else if (problem.edgeId) {
      selectEdge(problem.edgeId);
      onShowEdge(problem.edgeId);
    }
  };
  return (
    <AgentflowWorkspaceBottomPanel
      revisions={revisions}
      runContent={<AgentflowRunPanel runSeed={runSeed} onSave={onSave} onShowNode={onShowNode} />}
      onShowProblem={showProblem}
      onRetryValidation={onRetryValidation ?? requestValidation}
      onSave={() => onSave?.()}
      onOpenRevision={(revision) => onOpenRevision?.(revision)}
      onPublishRevision={(revision) => onPublishRevision?.(revision)}
      onNewRun={() => {
        setRunSeed((value) => value + 1);
        resetRunState();
      }}
    />
  );
}
