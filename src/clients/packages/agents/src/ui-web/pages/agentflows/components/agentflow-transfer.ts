import type { AgentflowDefinition } from "../../../../types/agentflow";
import type { AgentflowEditorDocument } from "./agentflow-document";
import { AGENTFLOW_NODE_META } from "./agentflow-node-meta";

export type AgentflowTransfer = {
  name: string;
  description: string | null;
  definition: AgentflowDefinition;
};

export type AgentflowTransferError = {
  field: "json" | "definition.nodes" | "definition.edges" | "nodeId" | "edgeId" | "kind";
  value: string;
};

export function createAgentflowDefinition(document: AgentflowEditorDocument): AgentflowDefinition {
  return { nodes: document.nodes, edges: document.edges };
}

export function createAgentflowTransfer(document: AgentflowEditorDocument): AgentflowTransfer {
  return {
    name: document.name,
    description: document.description || null,
    definition: createAgentflowDefinition(document),
  };
}

function readRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === "object" && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

function addIdErrors(
  entries: unknown[],
  field: "nodeId" | "edgeId",
  errors: AgentflowTransferError[],
): void {
  const seen = new Set<string>();
  const reported = new Set<string>();
  for (const entry of entries) {
    const id = readRecord(entry)?.[field];
    if (typeof id !== "string" || id.trim().length === 0) {
      errors.push({ field, value: String(id ?? "") });
    } else if (seen.has(id) && !reported.has(id)) {
      errors.push({ field, value: id });
      reported.add(id);
    } else {
      seen.add(id);
    }
  }
}

export function parseAgentflowTransfer(
  text: string,
): { ok: true; transfer: AgentflowTransfer } | { ok: false; errors: AgentflowTransferError[] } {
  let input: Record<string, unknown> | undefined;
  try {
    input = readRecord(JSON.parse(text));
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    return { ok: false, errors: [{ field: "json", value: "" }] };
  }

  const errors: AgentflowTransferError[] = [];
  const definition = readRecord(input?.definition);
  const nodes = definition?.nodes;
  const edges = definition?.edges;
  if (!Array.isArray(nodes)) errors.push({ field: "definition.nodes", value: "" });
  if (!Array.isArray(edges)) errors.push({ field: "definition.edges", value: "" });
  if (!Array.isArray(nodes) || !Array.isArray(edges)) return { ok: false, errors };
  for (const entry of nodes) {
    const node = readRecord(entry);
    if (typeof node?.kind !== "string" || !Object.hasOwn(AGENTFLOW_NODE_META, node.kind)) {
      errors.push({ field: "kind", value: `${String(node?.nodeId)}: ${String(node?.kind)}` });
    }
  }
  addIdErrors(nodes, "nodeId", errors);
  addIdErrors(edges, "edgeId", errors);

  if (errors.length > 0) return { ok: false, errors };
  return {
    ok: true,
    transfer: {
      name: typeof input?.name === "string" ? input.name : "",
      description: typeof input?.description === "string" ? input.description : null,
      definition: { nodes, edges },
    },
  };
}
