"use client";

import * as React from "react";
import type { AgentflowNodeKind, AgentflowPosition } from "../../../../types/agentflow";
import { copyNodes, deleteNodes, pasteNodes } from "./agentflow-clipboard";
import { addMember, createNode, type NodeReference } from "./agentflow-document";
import { useAgentflowEditorStoreApi, type AgentflowEditorStore } from "./agentflow-editor-store";
import { AGENTFLOW_TEMPLATES } from "./agentflow-templates";
import type { useAgentflowArrangement } from "./use-agentflow-arrangement";

type FindNodePosition = (position?: AgentflowPosition) => AgentflowPosition;

export function useAgentflowCanvasCommands(findPosition: FindNodePosition) {
  const store = useAgentflowEditorStoreApi();
  return React.useMemo(() => createCommands(store, findPosition), [store, findPosition]);
}

function createCommands(store: AgentflowEditorStore, findPosition: FindNodePosition) {
  return {
    addNode(kind: AgentflowNodeKind, reference?: NodeReference, position?: AgentflowPosition) {
      const { updateDocument, selectNodes } = store.getState();
      const node = createNode(kind, findPosition(position), reference);
      updateDocument((current) => ({ ...current, nodes: [...current.nodes, node] }));
      selectNodes([node.nodeId]);
      return node;
    },
    removeNodes(nodeIds: string[]) {
      const { updateDocument, clearSelection } = store.getState();
      updateDocument((current) => deleteNodes(current, nodeIds));
      clearSelection();
    },
    duplicate(nodeIds: string[]) {
      const { document, updateDocument, selectNodes } = store.getState();
      const copied = copyNodes(document, nodeIds);
      if (!copied) return;
      const result = pasteNodes(document, copied, { offset: 40 });
      updateDocument(() => result.document);
      selectNodes(result.nodeIds);
    },
    addNodeMember(nodeId: string, reference: NodeReference) {
      const { document, updateDocument, selectMember } = store.getState();
      const next = addMember(document, nodeId, reference);
      const node = next.nodes.find((item) => item.nodeId === nodeId);
      const member = node && "members" in node ? node.members.at(-1) : undefined;
      updateDocument(() => next);
      if (member) selectMember({ nodeId, memberId: member.memberId });
    },
  };
}

export function useAgentflowTemplates(
  arrange: ReturnType<typeof useAgentflowArrangement>["arrange"],
  onApply: () => void,
) {
  const store = useAgentflowEditorStoreApi();
  return async (templateId: string) => {
    const { document, updateDocument } = store.getState();
    const template = AGENTFLOW_TEMPLATES.find((item) => item.id === templateId)!;
    const arranged = await arrange({ ...document, ...template.create() }, false);
    updateDocument((current) => ({
      ...arranged,
      name: current.name,
      description: current.description,
    }));
    onApply();
  };
}

export function useAgentflowCanvasClipboard() {
  const store = useAgentflowEditorStoreApi();
  return React.useMemo(
    () => ({
      copy() {
        const { document, selectedNodeIds, setClipboard } = store.getState();
        setClipboard(copyNodes(document, selectedNodeIds));
      },
      paste(position?: AgentflowPosition) {
        const { document, clipboard, updateDocument, setClipboard, selectNodes } = store.getState();
        if (!clipboard) return;
        const result = pasteNodes(document, clipboard, position ? { position } : { offset: 40 });
        updateDocument(() => result.document);
        setClipboard(copyNodes(result.document, result.nodeIds));
        selectNodes(result.nodeIds);
      },
    }),
    [store],
  );
}
