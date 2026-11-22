"use client";

import * as React from "react";
import { useStore } from "zustand";
import { createStore, type StoreApi } from "zustand/vanilla";

import type { AiMessage } from "@agw/api";
import type { ExecutionStatusEvent } from "@agw/execution-core";
import type { AgentflowNode } from "../../../../types/agentflow";
import { EMPTY_PROBLEMS_STATE, type AgentflowProblemsState } from "./agentflow-problems";
import {
  EMPTY_RUN_STATE,
  applyAgentflowRunMessage,
  applyAgentflowRunStatus,
  type AgentflowRunState,
} from "./agentflow-run-state";
import type { AgentflowClipboard } from "./agentflow-clipboard";
import type { AgentflowEditorDocument } from "./agentflow-document";

const MAX_HISTORY_ENTRIES = 100;

type HistoryGroup = { key: string; before: AgentflowEditorDocument };
export type AgentflowEditorHistoryMode = "atomic" | "ephemeral" | { group: string };

export type AgentflowEditorState = {
  document: AgentflowEditorDocument;
  past: AgentflowEditorDocument[];
  future: AgentflowEditorDocument[];
  historyGroup: HistoryGroup | null;
  baselineFingerprint: string;
  isDirty: boolean;
  isSaving: boolean;
  agentflowId: string | null;
  baseRevision: number;
  publishedRevision: number;
  selectedNodeIds: string[];
  selectedEdgeId: string | null;
  selectedMember: { nodeId: string; memberId: string } | null;
  clipboard: AgentflowClipboard | null;
  problems: AgentflowProblemsState;
  runState: AgentflowRunState;
  panelTab: "problems" | "run" | "versions";
  panelExpanded: boolean;
  validationRequestId: number;
  updateDocument(
    update: (document: AgentflowEditorDocument) => AgentflowEditorDocument,
    historyMode?: AgentflowEditorHistoryMode,
  ): void;
  commitHistoryGroup(): void;
  undo(): void;
  redo(): void;
  markSaved(saved: { agentflowId: string; latestRevision: number }): void;
  loadDocument(saved: {
    document: AgentflowEditorDocument;
    agentflowId: string;
    baseRevision: number;
    publishedRevision: number;
  }): void;
  setBaseRevision(revision: number): void;
  setSaving(isSaving: boolean): void;
  setPublishedRevision(revision: number): void;
  selectNodes(nodeIds: string[]): void;
  selectEdge(edgeId: string | null): void;
  selectMember(member: { nodeId: string; memberId: string } | null): void;
  clearSelection(): void;
  setClipboard(clipboard: AgentflowClipboard | null): void;
  setProblems(problems: AgentflowProblemsState): void;
  applyRunStatus(status: ExecutionStatusEvent): void;
  applyRunMessage(message: AiMessage): void;
  resetRunState(): void;
  showPanel(tab: "problems" | "run" | "versions"): void;
  setPanelExpanded(expanded: boolean): void;
  requestValidation(): void;
};

export type AgentflowEditorStore = StoreApi<AgentflowEditorState>;

export function getAgentflowDocumentFingerprint(document: AgentflowEditorDocument): string {
  return JSON.stringify({
    name: document.name,
    description: document.description,
    nodes: document.nodes,
    edges: document.edges,
  });
}

function cloneDocument(document: AgentflowEditorDocument): AgentflowEditorDocument {
  return {
    ...document,
    nodes: document.nodes.map(
      (node) =>
        ({
          ...node,
          position: { ...node.position },
          ...("members" in node ? { members: node.members.map((member) => ({ ...member })) } : {}),
          ...("choices" in node ? { choices: node.choices.map((choice) => ({ ...choice })) } : {}),
        }) as AgentflowNode,
    ),
    edges: document.edges.map((edge) => ({
      ...edge,
      conditions: edge.conditions.map((condition) => ({ ...condition })),
    })),
  };
}

function appendHistory(history: AgentflowEditorDocument[], document: AgentflowEditorDocument) {
  return [...history, cloneDocument(document)].slice(-MAX_HISTORY_ENTRIES);
}

function reconcileSelection(state: AgentflowEditorState): AgentflowEditorState {
  const nodeIds = new Set(state.document.nodes.map((node) => node.nodeId));
  const edgeIds = new Set(state.document.edges.map((edge) => edge.edgeId));
  const selectedNodeIds = state.selectedNodeIds.filter((nodeId) => nodeIds.has(nodeId));
  const selectedEdgeId =
    state.selectedEdgeId && edgeIds.has(state.selectedEdgeId) ? state.selectedEdgeId : null;
  const selectedMember =
    state.selectedMember &&
    state.document.nodes.some(
      (node) =>
        node.nodeId === state.selectedMember?.nodeId &&
        "members" in node &&
        node.members.some((member) => member.memberId === state.selectedMember?.memberId),
    )
      ? state.selectedMember
      : null;
  return { ...state, selectedNodeIds, selectedEdgeId, selectedMember };
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

export function createAgentflowEditorStore(initial: {
  document: AgentflowEditorDocument;
  agentflowId: string | null;
  baseRevision: number;
  publishedRevision: number;
}): AgentflowEditorStore {
  const document = cloneDocument(initial.document);
  const baselineFingerprint = getAgentflowDocumentFingerprint(document);

  return createStore<AgentflowEditorState>((set) => ({
    document,
    past: [],
    future: [],
    historyGroup: null,
    baselineFingerprint,
    isDirty: false,
    isSaving: false,
    agentflowId: initial.agentflowId,
    baseRevision: initial.baseRevision,
    publishedRevision: initial.publishedRevision,
    selectedNodeIds: [],
    selectedEdgeId: null,
    selectedMember: null,
    clipboard: null,
    problems: EMPTY_PROBLEMS_STATE,
    runState: EMPTY_RUN_STATE,
    panelTab: "problems",
    panelExpanded: false,
    validationRequestId: 0,
    updateDocument: (update, historyMode = "atomic") => {
      set((currentState) => {
        if (currentState.isSaving) return currentState;
        const state =
          historyMode === "atomic" ||
          historyMode === "ephemeral" ||
          currentState.historyGroup?.key !== historyMode.group
            ? finalizeHistoryGroup(currentState)
            : currentState;
        const nextDocument = update(state.document);
        const changed =
          getAgentflowDocumentFingerprint(state.document) !==
          getAgentflowDocumentFingerprint(nextDocument);
        const isDirty = getAgentflowDocumentFingerprint(nextDocument) !== state.baselineFingerprint;

        if (historyMode === "ephemeral")
          return reconcileSelection({ ...state, document: nextDocument, isDirty });
        if (historyMode === "atomic") {
          return reconcileSelection({
            ...state,
            document: nextDocument,
            past: changed ? appendHistory(state.past, state.document) : state.past,
            future: changed ? [] : state.future,
            isDirty,
          });
        }

        return reconcileSelection({
          ...state,
          document: nextDocument,
          historyGroup: state.historyGroup ?? {
            key: historyMode.group,
            before: cloneDocument(state.document),
          },
          future: changed ? [] : state.future,
          isDirty,
        });
      });
    },
    commitHistoryGroup: () => set((state) => finalizeHistoryGroup(state)),
    undo: () =>
      set((currentState) => {
        if (currentState.isSaving) return currentState;
        const state = finalizeHistoryGroup(currentState);
        const previous = state.past.at(-1);
        if (!previous) return state;
        const document = cloneDocument(previous);
        return reconcileSelection({
          ...state,
          document,
          past: state.past.slice(0, -1),
          future: [cloneDocument(state.document), ...state.future],
          isDirty: getAgentflowDocumentFingerprint(document) !== state.baselineFingerprint,
        });
      }),
    redo: () =>
      set((currentState) => {
        if (currentState.isSaving) return currentState;
        const state = finalizeHistoryGroup(currentState);
        const next = state.future[0];
        if (!next) return state;
        const document = cloneDocument(next);
        return reconcileSelection({
          ...state,
          document,
          past: appendHistory(state.past, state.document),
          future: state.future.slice(1),
          isDirty: getAgentflowDocumentFingerprint(document) !== state.baselineFingerprint,
        });
      }),
    markSaved: (saved) =>
      set((currentState) => {
        const state = finalizeHistoryGroup(currentState);
        return {
          ...state,
          agentflowId: saved.agentflowId,
          baseRevision: saved.latestRevision,
          baselineFingerprint: getAgentflowDocumentFingerprint(state.document),
          isDirty: false,
        };
      }),
    loadDocument: (saved) => {
      const document = cloneDocument(saved.document);
      set((state) => ({
        ...state,
        document,
        past: [],
        future: [],
        historyGroup: null,
        baselineFingerprint: getAgentflowDocumentFingerprint(document),
        isDirty: false,
        agentflowId: saved.agentflowId,
        baseRevision: saved.baseRevision,
        publishedRevision: saved.publishedRevision,
        selectedNodeIds: [],
        selectedEdgeId: null,
        selectedMember: null,
      }));
    },
    setBaseRevision: (baseRevision) => set({ baseRevision }),
    setSaving: (isSaving) => set({ isSaving }),
    setPublishedRevision: (publishedRevision) => set({ publishedRevision }),
    selectNodes: (nodeIds) =>
      set((state) => ({
        selectedNodeIds: [...new Set(nodeIds)].filter((nodeId) =>
          state.document.nodes.some((node) => node.nodeId === nodeId),
        ),
        selectedEdgeId: null,
        selectedMember: null,
      })),
    selectEdge: (edgeId) =>
      set((state) => ({
        selectedNodeIds: [],
        selectedEdgeId:
          edgeId && state.document.edges.some((edge) => edge.edgeId === edgeId) ? edgeId : null,
        selectedMember: null,
      })),
    selectMember: (member) =>
      set((state) => {
        const exists =
          member &&
          state.document.nodes.some(
            (node) =>
              node.nodeId === member.nodeId &&
              "members" in node &&
              node.members.some((item) => item.memberId === member.memberId),
          );
        return {
          selectedNodeIds: [],
          selectedEdgeId: null,
          selectedMember: exists ? member : null,
        };
      }),
    clearSelection: () => set({ selectedNodeIds: [], selectedEdgeId: null, selectedMember: null }),
    setClipboard: (clipboard) => set({ clipboard }),
    setProblems: (problems) => set({ problems }),
    applyRunStatus: (status) =>
      set((state) => ({ runState: applyAgentflowRunStatus(state.runState, status) })),
    applyRunMessage: (message) =>
      set((state) => ({ runState: applyAgentflowRunMessage(state.runState, message) })),
    resetRunState: () => set({ runState: EMPTY_RUN_STATE }),
    showPanel: (panelTab) => set({ panelTab, panelExpanded: true }),
    setPanelExpanded: (panelExpanded) => set({ panelExpanded }),
    requestValidation: () =>
      set((state) => ({ validationRequestId: state.validationRequestId + 1 })),
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
  initial,
  children,
}: React.PropsWithChildren<{
  initial: {
    document: AgentflowEditorDocument;
    agentflowId: string | null;
    baseRevision: number;
    publishedRevision: number;
  };
}>) {
  const [store] = React.useState(() => createAgentflowEditorStore(initial));
  return (
    <AgentflowEditorStoreContext.Provider value={store}>
      {children}
    </AgentflowEditorStoreContext.Provider>
  );
}

export function useAgentflowEditorStore<T>(selector: (state: AgentflowEditorState) => T): T {
  return useStore(useAgentflowEditorStoreApi(), selector);
}

export function useAgentflowEditorStoreApi(): AgentflowEditorStore {
  const store = React.useContext(AgentflowEditorStoreContext);
  if (!store)
    throw new Error("useAgentflowEditorStore must be used within AgentflowEditorProvider.");
  return store;
}
