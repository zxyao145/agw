"use client";

import * as React from "react";
import { deleteEdge } from "./agentflow-clipboard";
import { removeMember } from "./agentflow-document";
import { useAgentflowEditorStoreApi, type AgentflowEditorState } from "./agentflow-editor-store";
import {
  isAgentflowEditorKeyboardTarget,
  isEditableKeyboardTarget,
  isRadixPopoverTarget,
} from "./agentflow-keyboard";

type CanvasKeyboardOptions = {
  searchInput: React.RefObject<HTMLInputElement | null>;
  transferOpen: boolean;
  clearSearch(): void;
  closeOverlay(): boolean;
  copy(): void;
  paste(): void;
  duplicate(nodeIds: string[]): void;
  deleteNodes(nodeIds: string[]): void;
};

export function useAgentflowCanvasKeyboard(options: CanvasKeyboardOptions) {
  const store = useAgentflowEditorStoreApi();
  React.useEffect(() => {
    const handle = (event: KeyboardEvent) => handleKeyDown(event, store.getState(), options);
    window.addEventListener("keydown", handle, true);
    return () => window.removeEventListener("keydown", handle, true);
  }, [options, store]);
}

function handleKeyDown(
  event: KeyboardEvent,
  state: AgentflowEditorState,
  options: CanvasKeyboardOptions,
) {
  const escape = event.key === "Escape";
  if ((escape && options.transferOpen) || isRadixPopoverTarget(event.target)) return;
  if (isEditableKeyboardTarget(event.target)) {
    if (escape) {
      if (event.target === options.searchInput.current) options.clearSearch();
      event.preventDefault();
    }
    return;
  }
  if (escape) {
    if (!options.closeOverlay() && !clearCanvasSelection(state)) return;
    event.preventDefault();
    event.stopPropagation();
    return;
  }
  if (!isAgentflowEditorKeyboardTarget(event.target) || event.altKey) return;
  if (event.key === "Delete" || event.key === "Backspace") {
    event.preventDefault();
    deleteSelection(state, options);
    return;
  }
  if ((event.metaKey || event.ctrlKey) && runShortcut(event.key.toLowerCase(), state, options)) {
    event.preventDefault();
  }
}

function clearCanvasSelection(state: AgentflowEditorState): boolean {
  if (!state.selectedNodeIds.length && !state.selectedEdgeId && !state.selectedMember) return false;
  state.clearSelection();
  return true;
}

function runShortcut(key: string, state: AgentflowEditorState, options: CanvasKeyboardOptions) {
  switch (key) {
    case "a":
      state.selectNodes(state.document.nodes.map((node) => node.nodeId));
      return true;
    case "c":
      options.copy();
      return true;
    case "v":
      options.paste();
      return true;
    case "d":
      options.duplicate(state.selectedNodeIds);
      return true;
    case "f":
      options.searchInput.current?.focus();
      return true;
    default:
      return false;
  }
}

function deleteSelection(state: AgentflowEditorState, options: CanvasKeyboardOptions) {
  const { selectedNodeIds, selectedEdgeId, selectedMember, updateDocument, selectMember } = state;
  if (selectedNodeIds.length) options.deleteNodes(selectedNodeIds);
  else if (selectedEdgeId) updateDocument((document) => deleteEdge(document, selectedEdgeId));
  else if (selectedMember) {
    updateDocument((document) =>
      removeMember(document, selectedMember.nodeId, selectedMember.memberId),
    );
    selectMember(null);
  }
}
