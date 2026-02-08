import { Node, Edge, isNode } from "reactflow";
import { ELKConstructorArguments, ElkExtendedEdge, ElkNode } from "elkjs";
import ELK from "elkjs/lib/elk.bundled.js";

const DEFAULT_WIDTH = 200;
const DEFAULT_HEIGHT = 48;

const elkArg: ELKConstructorArguments = {
  defaultLayoutOptions: {
    "elk.algorithm": "layered",
    "elk.direction": "RIGHT",
    "elk.spacing.nodeNode": "100", // 同层节点间距
    "elk.layered.spacing.nodeNodeBetweenLayers": "100", // 层间距
    "elk.layered.spacing": "100",
    "elk.layered.mergeEdges": "true",
    "elk.spacing": "200",
    "elk.spacing.individual": "200",
    "elk.edgeRouting": "SPLINES",
  },
};

const elk = new ELK();

export const createGraphLayout = async (
  orginNodes: Node<unknown, string | undefined>[],
  orginEdges: Edge<unknown>[]
) => {
  const nodes: ElkNode[] = [];
  const edges: ElkExtendedEdge[] = [];

  orginNodes.forEach((el) => {
    if (isNode(el)) {
      console.debug("isNode", el);
      nodes.push({
        id: el.id,
        width: el.width ?? DEFAULT_WIDTH,
        height: el.height ?? DEFAULT_HEIGHT,
      });
    }
  });
  orginEdges.forEach((el) => {
    edges.push({
      id: el.id,
      targets: [el.target],
      sources: [el.source],
    });
  });

  const graph = {
    id: "root",
    children: nodes,
    edges: edges,
  };
  const result = await elk
    .layout(graph, { layoutOptions: { ...elkArg.defaultLayoutOptions } })
    .then((layoutedGraph) => ({
      nodes: (layoutedGraph.children ?? []).map((node) => {
        const n = orginNodes.find((n) => n.id === node.id);
        return {
          ...n,
          position: { x: node.x, y: node.y },
        } as Node<unknown, string | undefined>;
      }),
      edges: (layoutedGraph.edges ?? []).map((edge) => {
        const e = orginEdges.find((n) => n.id === edge.id);
        return {
          ...e,
        } as Edge<unknown>;
      }),
    }));
  return result;
};
