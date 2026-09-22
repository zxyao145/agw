import assert from "node:assert/strict";
import test, { afterEach } from "node:test";
import { setupDomEnvironment, startApiServer } from "@agw/test-harness";

import type { AgentSelection } from "./agent-selector";

const { React, act, fireEvent, render, screen, waitFor } = await setupDomEnvironment();
const { configureApiRuntime, resetApiRuntime } = await import("@agw/api");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { AgentSelector } = await import("./agent-selector.tsx");

const PROJECT_ID = "11111111-1111-1111-1111-000000000099";

const agents = [
  { id: "agent-2", name: "ClaudeCode", displayName: "Claude Code", enable: true },
  { id: "agent-1", name: "GeneralAgent", displayName: "General Agent", enable: true },
];
const agentflows = [{ id: "flow-1", name: "Alpha Flow", enable: true }];

const api = await startApiServer({
  "GET /api/agents": agents,
  "GET /api/agentflows": agentflows,
});
configureApiRuntime({ baseUrl: api.baseUrl, token: null });
test.after(() => resetApiRuntime());

let activeClient: InstanceType<typeof QueryClient> | null = null;
afterEach(() => {
  activeClient?.clear();
  activeClient = null;
});

function renderSelector(
  props: {
    value?: AgentSelection | null;
    clearable?: boolean;
    placeholder?: string;
    selections?: AgentSelection[];
    clears?: number[];
  } = {},
) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  activeClient = client;
  return render(
    React.createElement(
      QueryClientProvider,
      { client },
      React.createElement(AgentSelector, {
        id: "chat-target",
        projectId: PROJECT_ID,
        value: props.value ?? null,
        clearable: props.clearable,
        placeholder: props.placeholder,
        onSelect: (selection: AgentSelection) => props.selections?.push(selection),
        onClear: () => props.clears?.push(1),
      }),
    ),
  );
}

function trigger() {
  return screen.getByRole("combobox", { name: "Select target" });
}

async function openPopup() {
  await act(async () => {
    fireEvent.click(trigger());
  });
  return screen.findByLabelText("Search agents or agentflows...");
}

test("agents and agentflows load from the server into grouped options", async () => {
  renderSelector();
  await openPopup();

  await waitFor(() => {
    assert.deepEqual(
      screen.getAllByRole("option").map((option) => option.textContent),
      ["Claude Code", "General Agent", "Alpha Flow"],
    );
  });
  assert.ok(screen.getByText("Agent"));
  assert.ok(screen.getByText("Agentflow"));
});

test("choosing an agent reports its type and id", async () => {
  const selections: AgentSelection[] = [];
  renderSelector({ selections });
  await openPopup();
  const option = await screen.findByRole("option", { name: "General Agent" });

  await act(async () => {
    fireEvent.click(option);
  });

  assert.deepEqual(selections, [{ agentType: 0, agentId: "agent-1" }]);
});

test("choosing an agentflow reports the agentflow type", async () => {
  const selections: AgentSelection[] = [];
  renderSelector({ selections });
  await openPopup();
  const option = await screen.findByRole("option", { name: "Alpha Flow" });

  await act(async () => {
    fireEvent.click(option);
  });

  assert.deepEqual(selections, [{ agentType: 1, agentId: "flow-1" }]);
});

test("the current selection is shown on the trigger", async () => {
  renderSelector({ value: { agentType: 0, agentId: "agent-2" } });

  await waitFor(() => assert.equal(trigger().textContent, "Claude Code"));
});

test("an agent is searchable by its name as well as its display name", async () => {
  renderSelector();
  const search = await openPopup();

  await act(async () => {
    fireEvent.change(search, { target: { value: "GeneralAgent" } });
  });

  await waitFor(() => {
    assert.deepEqual(
      screen.getAllByRole("option").map((option) => option.textContent),
      ["General Agent"],
    );
  });
});

test("a clearable selector reports the cleared selection", async () => {
  const clears: number[] = [];
  renderSelector({ value: { agentType: 0, agentId: "agent-2" }, clearable: true, clears });
  await openPopup();
  const clearAction = await screen.findByRole("button", { name: "Clear selection" });

  await act(async () => {
    fireEvent.click(clearAction);
  });

  assert.deepEqual(clears, [1]);
});

test("the caller's placeholder is shown while nothing is selected", async () => {
  renderSelector({ placeholder: "Inherit from project" });

  await waitFor(() => assert.equal(trigger().textContent, "Inherit from project"));
});

test("a failing catalog request surfaces the server error", async () => {
  const failing = await startApiServer({ "GET /api/agents": agents });
  configureApiRuntime({ baseUrl: failing.baseUrl, token: null });
  try {
    renderSelector();
    await openPopup();

    await waitFor(() => assert.ok(screen.getByText(/Not Found/)));
    assert.equal(screen.queryByRole("option"), null);
  } finally {
    configureApiRuntime({ baseUrl: api.baseUrl, token: null });
    await failing.close();
  }
});
