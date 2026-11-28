import assert from "node:assert/strict";
import test from "node:test";
import { EMPTY_AGENTFLOW_NODE_RUN_STATE } from "@agw/chat-core";
import { selectAsideAgentflow } from "./aside-agentflow";

const flow = { id: "flow", type: "agentflow" } as const;
const agent = { id: "agent", type: "agent" } as const;
const catalog = [{ id: "flow", publishedRevision: 3 }];
const lastRun = {
  ...EMPTY_AGENTFLOW_NODE_RUN_STATE,
  turnId: "turn",
  agentflowId: "flow",
  agentflowRevision: 2,
} as const;

test("a running Agentflow takes precedence over the selected Agent", () => {
  const run = { ...lastRun, turnActive: true };
  assert.deepEqual(selectAsideAgentflow(run, agent, catalog), {
    agentflowId: "flow",
    revision: 2,
    run,
  });
});

test("a matching selection keeps the last run and its revision", () => {
  assert.deepEqual(selectAsideAgentflow(lastRun, flow, catalog), {
    agentflowId: "flow",
    revision: 2,
    run: lastRun,
  });
});

test("a selected Agentflow uses its explicit or published revision with empty status", () => {
  for (const [target, revision] of [
    [flow, 3],
    [{ ...flow, agentflowRevision: 5 }, 5],
  ] as const) {
    assert.deepEqual(selectAsideAgentflow(EMPTY_AGENTFLOW_NODE_RUN_STATE, target, catalog), {
      agentflowId: "flow",
      revision,
      run: EMPTY_AGENTFLOW_NODE_RUN_STATE,
    });
  }
});

test("an Agent selection or no selection hides a finished Agentflow", () => {
  assert.equal(selectAsideAgentflow(lastRun, agent, catalog), null);
  assert.equal(selectAsideAgentflow(lastRun, null, catalog), null);
});

test("an unpublished Agentflow is not shown", () => {
  assert.equal(
    selectAsideAgentflow(EMPTY_AGENTFLOW_NODE_RUN_STATE, flow, [
      { ...catalog[0]!, publishedRevision: 0 },
    ]),
    null,
  );
  assert.equal(
    selectAsideAgentflow({ ...lastRun, turnActive: true, agentflowRevision: 0 }, flow, catalog),
    null,
  );
});
