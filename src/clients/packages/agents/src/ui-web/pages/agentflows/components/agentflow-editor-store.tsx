"use client";

import * as React from "react";
import type { Edge, Node } from "reactflow";
import { useStore } from "zustand";
import { createStore, type StoreApi } from "zustand/vanilla";

import { AgentflowNodeKind } from "../../../../types/agentflow";
import { createDefaultEdgeData, type AgentflowEdgeData } from "./agentflow-edge-routing";
import { isBlockNodeKind, type BlockMemberView } from "./block-membership";

const MAX_HISTORY_ENTRIES = 100;

export type AgentflowEditorNodeData = {
  kind: AgentflowNodeKind;
  title: string;
  relateId: string | null;
  instructions: string;
  configJson: string;
  presentation?: {
    disableHandles?: boolean;
    member?: BlockMemberView;
    members?: BlockMemberView[];
    onDelete?: (nodeId: string) => void;
    onOpenBlock?: (blockId: string) => void;
  };
};

export type AgentflowEditorEdgeData = AgentflowEdgeData;

export type AgentflowEditorDocument = {
  name: string;
  description: string;
  summaryModelProviderId: string;
  nodes: Node<AgentflowEditorNodeData>[];
  edges: Edge<AgentflowEditorEdgeData>[];
};

export type AgentflowEditorCanvasScope = { kind: "root" } | { kind: "block"; blockId: string };

type HistoryGroup = {
  key: string;
  before: AgentflowEditorDocument;
};

export type AgentflowEditorHistoryMode = "atomic" | "ephemeral" | { group: string };

export type AgentflowEditorState = {
  document: AgentflowEditorDocument;
  past: AgentflowEditorDocument[];
  future: AgentflowEditorDocument[];
  historyGroup: HistoryGroup | null;
  baselineFingerprint: string;
  isDirty: boolean;
  isSaving: boolean;
  selectedNodeId: string | null;
  selectedEdgeId: string | null;
  canvasScope: AgentflowEditorCanvasScope;
  pendingFocusNodeId: string | null;
  updateDocument(
    update: (document: AgentflowEditorDocument) => AgentflowEditorDocument,
    historyMode?: AgentflowEditorHistoryMode,
  ): void;
  commitHistoryGroup(): void;
  undo(): void;
  redo(): void;
  markSaved(): void;
  setSaving(isSaving: boolean): void;
  selectNode(nodeId: string | null): void;
  selectEdge(edgeId: string | null): void;
  clearSelection(): void;
  setCanvasScope(scope: AgentflowEditorCanvasScope): void;
  setPendingFocusNodeId(nodeId: string | null): void;
};

export type AgentflowEditorStore = StoreApi<AgentflowEditorState>;

function cloneDocument(document: AgentflowEditorDocument): AgentflowEditorDocument {
  return {
    ...document,
    nodes: document.nodes.map((node) => ({
      ...node,
      position: { ...node.position },
      data: { ...node.data, presentation: undefined },
      style: node.style ? { ...node.style } : node.style,
    })),
    edges: document.edges.map((edge) => ({
      ...edge,
      data: edge.data ? { ...edge.data } : edge.data,
      style: edge.style ? { ...edge.style } : edge.style,
    })),
  };
}

export function getAgentflowDocumentFingerprint(document: AgentflowEditorDocument): string {
  return JSON.stringify({
    name: document.name,
    description: document.description || null,
    summaryModelProviderId: document.summaryModelProviderId || null,
    nodes: document.nodes.map((node) => ({
      nodeId: node.id,
      kind: node.data.kind,
      relateId: node.data.relateId,
      name: node.data.title || null,
      position: { x: node.position.x, y: node.position.y },
      instructions: node.data.instructions || null,
      configJson: node.data.configJson || null,
    })),
    edges: document.edges.map((edge) => {
      const data = { ...createDefaultEdgeData(), ...edge.data };
      return {
        edgeId: edge.id,
        sourceNodeId: edge.source,
        targetNodeId: edge.target,
        kind: data.kind,
        label: data.label || null,
        conditionJson: data.conditionJson || null,
        configJson: data.configJson || null,
      };
    }),
  });
}

function appendHistory(
  history: AgentflowEditorDocument[],
  document: AgentflowEditorDocument,
): AgentflowEditorDocument[] {
  return [...history, cloneDocument(document)].slice(-MAX_HISTORY_ENTRIES);
}

function reconcileUiState(state: AgentflowEditorState): AgentflowEditorState {
  const nodeIds = new Set(state.document.nodes.map((node) => node.id));
  const edgeIds = new Set(state.document.edges.map((edge) => edge.id));
  const activeBlockId = state.canvasScope.kind === "block" ? state.canvasScope.blockId : null;
  const activeBlock = activeBlockId
    ? state.document.nodes.find((node) => node.id === activeBlockId)
    : null;
  const canvasScope =
    state.canvasScope.kind === "block" && (!activeBlock || !isBlockNodeKind(activeBlock.data.kind))
      ? ({ kind: "root" } as const)
      : state.canvasScope;

  return {
    ...state,
    canvasScope,
    selectedNodeId:
      state.selectedNodeId && nodeIds.has(state.selectedNodeId) ? state.selectedNodeId : null,
    selectedEdgeId:
      canvasScope.kind === "root" && state.selectedEdgeId && edgeIds.has(state.selectedEdgeId)
        ? state.selectedEdgeId
        : null,
    pendingFocusNodeId:
      state.pendingFocusNodeId && nodeIds.has(state.pendingFocusNodeId)
        ? state.pendingFocusNodeId
        : null,
  };
}

function finalizeHistoryGroup(state: AgentflowEditorState): AgentflowEditorState {
  if (!state.historyGroup) return state;

  const changed =
    getAgentflowDocumentFingerprint(state.historyGroup.before) !==
    getAgentflowDocumentFingerprint(state.document);
  return {
    ...state,
    historyGroup: null,
    past: changed ? appendHistory(state.past, state.historyGroup.before) : state.past,
  };
}

export function createAgentflowEditorStore(
  initialDocument: AgentflowEditorDocument,
): AgentflowEditorStore {
  const document = cloneDocument(initialDocument);
  const baselineFingerprint = getAgentflowDocumentFingerprint(document);

  return createStore<AgentflowEditorState>((set) => ({
    document,
    past: [],
    future: [],
    historyGroup: null,
    baselineFingerprint,
    isDirty: false,
    isSaving: false,
    selectedNodeId: document.nodes[0]?.id ?? null,
    selectedEdgeId: null,
    canvasScope: { kind: "root" },
    pendingFocusNodeId: null,
    updateDocument: (update, historyMode = "atomic") => {
      set((currentState) => {
        if (historyMode === "ephemeral") {
          const nextDocument = update(currentState.document);
          return reconcileUiState({
            ...currentState,
            document: nextDocument,
            isDirty:
              getAgentflowDocumentFingerprint(nextDocument) !== currentState.baselineFingerprint,
          });
        }

        let state = currentState;
        if (historyMode === "atomic" || state.historyGroup?.key !== historyMode.group) {
          state = finalizeHistoryGroup(state);
        }

        const before = state.document;
        const nextDocument = update(before);
        const changed =
          getAgentflowDocumentFingerprint(before) !== getAgentflowDocumentFingerprint(nextDocument);

        if (historyMode === "atomic") {
          return reconcileUiState({
            ...state,
            document: nextDocument,
            past: changed ? appendHistory(state.past, before) : state.past,
            future: changed ? [] : state.future,
            isDirty: getAgentflowDocumentFingerprint(nextDocument) !== state.baselineFingerprint,
          });
        }

        return reconcileUiState({
          ...state,
          document: nextDocument,
          historyGroup: state.historyGroup ?? {
            key: historyMode.group,
            before: cloneDocument(before),
          },
          future: changed ? [] : state.future,
          isDirty: getAgentflowDocumentFingerprint(nextDocument) !== state.baselineFingerprint,
        });
      });
    },
    commitHistoryGroup: () => set((state) => finalizeHistoryGroup(state)),
    undo: () => {
      set((currentState) => {
        const state = finalizeHistoryGroup(currentState);
        const previous = state.past.at(-1);
        if (!previous) return state;

        const document = cloneDocument(previous);
        return reconcileUiState({
          ...state,
          document,
          past: state.past.slice(0, -1),
          future: [cloneDocument(state.document), ...state.future],
          isDirty: getAgentflowDocumentFingerprint(document) !== state.baselineFingerprint,
        });
      });
    },
    redo: () => {
      set((currentState) => {
        const state = finalizeHistoryGroup(currentState);
        const next = state.future[0];
        if (!next) return state;

        const document = cloneDocument(next);
        return reconcileUiState({
          ...state,
          document,
          past: appendHistory(state.past, state.document),
          future: state.future.slice(1),
          isDirty: getAgentflowDocumentFingerprint(document) !== state.baselineFingerprint,
        });
      });
    },
    markSaved: () => {
      set((currentState) => {
        const state = finalizeHistoryGroup(currentState);
        return {
          ...state,
          baselineFingerprint: getAgentflowDocumentFingerprint(state.document),
          isDirty: false,
        };
      });
    },
    setSaving: (isSaving) => set({ isSaving }),
    selectNode: (nodeId) => {
      set((state) => ({
        selectedNodeId:
          nodeId && state.document.nodes.some((node) => node.id === nodeId) ? nodeId : null,
        selectedEdgeId: null,
      }));
    },
    selectEdge: (edgeId) => {
      set((state) => ({
        selectedNodeId: null,
        selectedEdgeId:
          state.canvasScope.kind === "root" &&
          edgeId &&
          state.document.edges.some((edge) => edge.id === edgeId)
            ? edgeId
            : null,
      }));
    },
    clearSelection: () => set({ selectedNodeId: null, selectedEdgeId: null }),
    setCanvasScope: (scope) => {
      set((state) => {
        if (scope.kind === "root") return { canvasScope: scope, selectedEdgeId: null };
        const block = state.document.nodes.find((node) => node.id === scope.blockId);
        return block && isBlockNodeKind(block.data.kind)
          ? { canvasScope: scope, selectedEdgeId: null }
          : { canvasScope: { kind: "root" }, selectedEdgeId: null };
      });
    },
    setPendingFocusNodeId: (nodeId) => {
      set((state) => ({
        pendingFocusNodeId:
          nodeId && state.document.nodes.some((node) => node.id === nodeId) ? nodeId : null,
      }));
    },
  }));
}

export function selectCanUndo(state: AgentflowEditorState): boolean {
  if (state.past.length > 0) return true;
  if (!state.historyGroup) return false;
  return (
    getAgentflowDocumentFingerprint(state.historyGroup.before) !==
    getAgentflowDocumentFingerprint(state.document)
  );
}

export function selectCanRedo(state: AgentflowEditorState): boolean {
  return state.future.length > 0;
}

const AgentflowEditorStoreContext = React.createContext<AgentflowEditorStore | null>(null);

export function AgentflowEditorProvider({
  initialDocument,
  children,
}: React.PropsWithChildren<{ initialDocument: AgentflowEditorDocument }>) {
  const [store] = React.useState(() => createAgentflowEditorStore(initialDocument));
  return (
    <AgentflowEditorStoreContext.Provider value={store}>
      {children}
    </AgentflowEditorStoreContext.Provider>
  );
}

export function useAgentflowEditorStore<T>(selector: (state: AgentflowEditorState) => T): T {
  const store = React.useContext(AgentflowEditorStoreContext);
  if (!store) {
    throw new Error("useAgentflowEditorStore must be used within AgentflowEditorProvider.");
  }
  return useStore(store, selector);
}
