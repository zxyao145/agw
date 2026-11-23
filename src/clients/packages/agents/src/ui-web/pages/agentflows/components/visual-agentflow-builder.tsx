"use client";

import * as React from "react";
import { ReactFlowProvider } from "reactflow";
import type { AgentflowNodeCardData } from "./agentflow-node-card";
import { AgentflowBuilderBottomPanel } from "./agentflow-builder-bottom-panel";
import { AgentflowBuilderLayout } from "./agentflow-builder-layout";
import { useAgentflowCanvasContextMenu } from "./agentflow-canvas-context-menu";
import { AgentflowCanvasView, type AgentflowCanvasViewProps } from "./agentflow-canvas-view";
import { AgentflowInspector } from "./agentflow-inspector";
import { AgentflowNodeLibrary } from "./agentflow-node-library";
import { useAgentflowTransferDialogs } from "./agentflow-transfer-dialogs";
import {
  useAgentflowCanvasClipboard,
  useAgentflowCanvasCommands,
} from "./use-agentflow-canvas-commands";
import { useAgentflowCanvasInteractions } from "./use-agentflow-canvas-interactions";
import { useAgentflowCanvasKeyboard } from "./use-agentflow-canvas-keyboard";
import {
  useAgentflowCanvasDisplay,
  useAgentflowCanvasWorkspace,
} from "./use-agentflow-canvas-workspace";
import { useAgentflowConnections } from "./use-agentflow-connections";
import { useAgentflowNodePicker } from "./use-agentflow-node-picker";
import "reactflow/dist/style.css";

export type AgentflowBuilderActions = {
  openTemplates(): void;
  beginImport(): void;
  beginExport(): void;
};

type CanvasLookups = AgentflowNodeCardData["lookups"];
type BuilderActionsRef = React.Ref<AgentflowBuilderActions> | undefined;
type AgentflowBuilderProps = CanvasLookups &
  Omit<React.ComponentProps<typeof AgentflowBuilderBottomPanel>, "onShowNode" | "onShowEdge"> & {
    actionsRef?: BuilderActionsRef;
  };

export function VisualAgentflowBuilder(props: AgentflowBuilderProps) {
  return (
    <ReactFlowProvider>
      <AgentflowBuilderCanvas {...props} />
    </ReactFlowProvider>
  );
}

function AgentflowBuilderCanvas({
  agents,
  agentflows,
  modelProviders,
  actionsRef,
  ...bottomProps
}: AgentflowBuilderProps) {
  const lookups = React.useMemo(
    () => ({ agents, agentflows, modelProviders }),
    [agents, agentflows, modelProviders],
  );
  const { view, commands, overlays, showEdge } = useBuilderCanvas(lookups, actionsRef);
  return (
    <AgentflowBuilderLayout
      library={
        <AgentflowNodeLibrary
          agents={agents}
          agentflows={agentflows}
          currentAgentflowId={view.currentAgentflowId}
          onAddNode={commands.addNode}
        />
      }
      canvas={<AgentflowCanvasView {...view} />}
      bottomPanel={
        <AgentflowBuilderBottomPanel
          {...bottomProps}
          onShowNode={view.onSearchResult}
          onShowEdge={showEdge}
        />
      }
      inspector={
        <AgentflowInspector
          {...lookups}
          onAddMember={commands.addNodeMember}
          onDuplicateNodes={commands.duplicate}
          onDeleteNodes={commands.removeNodes}
        />
      }
    >
      {overlays}
    </AgentflowBuilderLayout>
  );
}

function useBuilderCanvas(lookups: CanvasLookups, actionsRef: BuilderActionsRef) {
  const workspace = useAgentflowCanvasWorkspace();
  const commands = useAgentflowCanvasCommands(workspace.navigation.positionNode);
  const picker = useAgentflowNodePicker({
    canvasRef: workspace.view.canvasRef,
    liveNodes: workspace.liveNodes,
    positionNode: workspace.navigation.positionNode,
    arrange: workspace.arrange,
  });
  const { connectionSourceNodeId, ...connections } = useAgentflowConnections(picker.openConnect);
  const display = useAgentflowCanvasDisplay(
    workspace,
    lookups,
    commands,
    picker.openInsert,
    connectionSourceNodeId,
  );
  const overlays = useBuilderOverlays(workspace, commands, picker, actionsRef);
  const selection = useAgentflowCanvasInteractions({
    addNode: commands.addNode,
    closeMenu: overlays.closeMenu,
  });
  const view: AgentflowCanvasViewProps = {
    agents: lookups.agents,
    agentflows: lookups.agentflows,
    ...workspace.view,
    ...display.view,
    ...connections,
    ...selection,
    ...overlays.view,
    menuOpen: picker.menuOpen,
    menuPosition: picker.menuPosition,
    onMenuOpenChange: picker.onMenuOpenChange,
    onSelectMenuNode: picker.onSelectMenuNode,
  };
  return { view, commands, showEdge: display.showEdge, overlays: overlays.content };
}

type Workspace = ReturnType<typeof useAgentflowCanvasWorkspace>;
type Commands = ReturnType<typeof useAgentflowCanvasCommands>;
type Picker = ReturnType<typeof useAgentflowNodePicker>;

function useBuilderOverlays(
  workspace: Workspace,
  commands: Commands,
  picker: Picker,
  actionsRef: BuilderActionsRef,
) {
  const {
    arrange,
    clearSearch,
    view: { searchInputRef },
  } = workspace;
  const transfer = useAgentflowTransferDialogs((document) => arrange(document, false));
  const { copy, paste } = useAgentflowCanvasClipboard();
  const { content, close, openTemplates, applyTemplate, ...handlers } =
    useAgentflowCanvasContextMenu({
      ...picker,
      arrange,
      duplicate: commands.duplicate,
      deleteNodes: commands.removeNodes,
      paste,
    });
  React.useImperativeHandle(actionsRef, () => ({
    openTemplates,
    beginImport: transfer.beginImport,
    beginExport: transfer.beginExport,
  }));
  useAgentflowCanvasKeyboard({
    searchInput: searchInputRef,
    transferOpen: transfer.isOpen,
    clearSearch,
    closeOverlay: () => closeOverlay(close, picker),
    copy,
    paste,
    duplicate: commands.duplicate,
    deleteNodes: commands.removeNodes,
  });
  return {
    content: (
      <>
        {content}
        {transfer.dialogs}
      </>
    ),
    closeMenu: close,
    view: { ...handlers, onUseTemplate: (id: string) => void applyTemplate(id) },
  };
}

function closeOverlay(closeMenu: () => boolean, picker: Picker) {
  if (closeMenu()) return true;
  if (!picker.menuOpen) return false;
  picker.close();
  return true;
}
