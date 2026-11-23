import type { AgentflowEdge, AgentflowNode } from "../../../../types/agentflow";
import {
  createEdge,
  createEmptyDocument,
  createId,
  createNode,
  type AgentflowEditorDocument,
} from "./agentflow-document";

export type AgentflowTemplate = {
  id: string;
  name: string;
  create(): Pick<AgentflowEditorDocument, "nodes" | "edges">;
};

type Connection = [number, number, Partial<AgentflowEdge>?];

function createTemplate(nodes: AgentflowNode[], connections: Connection[]) {
  const document = createEmptyDocument();
  document.nodes.push(...nodes);
  for (const [source, target, options] of connections) {
    document.edges.push({
      ...createEdge(document, document.nodes[source].nodeId, document.nodes[target].nodeId),
      ...options,
    });
  }
  return { nodes: document.nodes, edges: document.edges };
}

const agent = (name: string, x: number, y = 80): AgentflowNode => ({
  ...createNode("agent", { x, y }),
  name,
});
const output = (x: number) => createNode("output", { x, y: 80 });

export const AGENTFLOW_TEMPLATES: AgentflowTemplate[] = [
  {
    id: "sequential",
    name: "Sequential pipeline",
    create: () =>
      createTemplate(
        [agent("Agent 1", 380), agent("Agent 2", 680), output(980)],
        [
          [0, 1],
          [1, 2],
          [2, 3],
        ],
      ),
  },
  {
    id: "parallel",
    name: "Parallel, then join",
    create: () => {
      const graph = createTemplate(
        [agent("Agent A", 380, 40), agent("Agent B", 380, 240), agent("Agent C", 680), output(980)],
        [
          [0, 1],
          [0, 2],
          [1, 3, { isFanInBarrier: true }],
          [2, 3, { isFanInBarrier: true }],
          [3, 4],
        ],
      );
      graph.nodes[0].routingStrategy = "fanOut";
      return graph;
    },
  },
  {
    id: "review-loop",
    name: "Review loop",
    create: () =>
      createTemplate(
        [
          agent("Author", 380),
          { ...agent("Reviewer", 680), routingStrategy: "switch" },
          output(980),
        ],
        [
          [0, 1],
          [1, 2],
          [2, 3, { conditions: [{ field: "contains", value: "approved", path: "" }] }],
          [2, 1, { isSwitchDefault: true, switchCaseOrder: 0 }],
        ],
      ),
  },
  {
    id: "human-approval",
    name: "Human approval",
    create: () => {
      const approve = { choiceId: createId("choice"), label: "Approve" };
      const changes = { choiceId: createId("choice"), label: "Request changes" };
      const gate: Extract<AgentflowNode, { kind: "humanGate" }> = {
        nodeId: createId("humanGate"),
        kind: "humanGate",
        name: "Human Gate",
        position: { x: 680, y: 80 },
        routingStrategy: "choice",
        humanPrompt: "Review the draft.",
        choices: [approve, changes],
        allowResponseText: true,
      };
      return createTemplate(
        [agent("Draft", 380), gate, output(980), agent("Revise", 980, 240)],
        [
          [0, 1],
          [1, 2],
          [2, 3, { choiceId: approve.choiceId }],
          [2, 4, { choiceId: changes.choiceId }],
          [4, 2],
        ],
      );
    },
  },
  {
    id: "magentic",
    name: "Magentic team",
    create: () =>
      createTemplate(
        [createNode("magenticBlock", { x: 380, y: 80 }), output(680)],
        [
          [0, 1],
          [1, 2],
        ],
      ),
  },
];
