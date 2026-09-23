import assert from "node:assert/strict";
import test, { afterEach } from "node:test";
import { setupDomEnvironment, startApiServer, type ApiRequestRecord } from "@agw/test-harness";

const { React, act, fireEvent, render, screen, waitFor } = await setupDomEnvironment();
const { configureApiRuntime, resetApiRuntime } = await import("@agw/api");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { TooltipProvider } = await import("@agw/components");
const { default: AgentsPage } = await import("./page.tsx");

const agent = {
  id: "11111111-1111-1111-1111-000000000001",
  name: "reviewer",
  displayName: "Reviewer",
  description: "Reviews the current diff",
  systemPrompt: "You review code.",
  type: 0,
  enable: true,
  tools: [],
  skills: [],
  mcpToolServers: [],
  connections: [],
  environmentVariables: {},
  modelProviderId: "provider-1",
  summaryModelProviderId: null,
  enableSummary: false,
  responseSchema: null,
  extra: null,
  createTime: "2026-09-20T01:00:00Z",
  updateTime: "2026-09-21T01:00:00Z",
};
const modelProvider = {
  id: "provider-1",
  modelName: "claude-opus-5",
  providerName: "anthropic",
  providerType: "Anthropic",
};

const api = await startApiServer({
  "GET /api/agents/paged": { items: [agent], total: 1, pageIndex: 1, pageSize: 20 },
  "GET /api/model-providers": [modelProvider],
  "GET /api/agents/external-options": [],
  "GET /api/tools": [],
  "GET /api/mcp-tool-servers": [],
  "GET /api/skills": [],
  "GET /api/integrations/connections": [],
  "POST /api/agents": agent,
  "PUT /api/agents/{id}": agent,
  "PUT /api/agents/enabled": agent,
  "DELETE /api/agents/{id}": true,
});
configureApiRuntime({ baseUrl: api.baseUrl, token: null });
test.after(() => resetApiRuntime());

let activeClient: InstanceType<typeof QueryClient> | null = null;
afterEach(() => {
  activeClient?.clear();
  activeClient = null;
  api.requests.length = 0;
});

function writes(method: string, path: string): ApiRequestRecord[] {
  return api.requests.filter((request) => request.method === method && request.path === path);
}

async function renderPage() {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      // The default five-minute mutation garbage-collection timer keeps the test
      // process alive long after the last assertion.
      // 默认五分钟的 mutation 垃圾回收计时器会在最后一个断言之后长时间维持测试进程。
      mutations: { gcTime: 0 },
    },
  });
  activeClient = client;
  const view = render(
    React.createElement(
      QueryClientProvider,
      { client },
      React.createElement(TooltipProvider, null, React.createElement(AgentsPage)),
    ),
  );
  await screen.findByText("reviewer");
  return view;
}

async function openDialog(name: string) {
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name }));
  });
  return screen.findByRole("dialog");
}

test("the page lists the agents the server returns", async () => {
  await renderPage();

  assert.ok(screen.getByRole("heading", { name: "Agents", level: 1 }));
  assert.ok(screen.getByText("Reviewer"));
  assert.deepEqual(writes("GET", "/api/agents/paged")[0].query.get("pageSize"), "20");
});

test("creating an agent sends the metadata the operator filled in", async () => {
  await renderPage();
  await openDialog("Create");

  await act(async () => {
    fireEvent.change(screen.getByLabelText(/Display Name/), { target: { value: "Summarizer" } });
  });
  await act(async () => {
    fireEvent.click(screen.getByRole("combobox", { name: "Model Provider" }));
  });
  const provider = await screen.findByRole("option", { name: /claude-opus-5/ });
  await act(async () => {
    fireEvent.click(provider);
  });
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Create" }));
  });

  await waitFor(() => assert.equal(writes("POST", "/api/agents").length, 1));
  const body = writes("POST", "/api/agents")[0].body as Record<string, unknown>;
  assert.equal(body.displayName, "Summarizer");
  assert.equal(body.modelProviderId, "provider-1");
});

test("an agent without a model provider cannot be created", async () => {
  await renderPage();
  await openDialog("Create");

  await act(async () => {
    fireEvent.change(screen.getByLabelText(/Display Name/), { target: { value: "Summarizer" } });
  });

  assert.equal(screen.getByRole("button", { name: "Create" }).hasAttribute("disabled"), true);
});

test("editing an agent starts from its stored values and sends an update", async () => {
  await renderPage();
  const editAction = document.querySelectorAll("tbody button")[2];
  await act(async () => {
    fireEvent.click(editAction);
  });
  await screen.findByRole("dialog");

  assert.equal((screen.getByLabelText(/Display Name/) as HTMLInputElement).value, "Reviewer");
  await act(async () => {
    fireEvent.change(screen.getByLabelText(/Display Name/), { target: { value: "Reviewer 2" } });
  });
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Update" }));
  });

  await waitFor(() => assert.equal(writes("PUT", `/api/agents/${agent.id}`).length, 1));
  const body = writes("PUT", `/api/agents/${agent.id}`)[0].body as Record<string, unknown>;
  assert.equal(body.displayName, "Reviewer 2");
});

test("copying an agent asks for a unique name before creating it", async () => {
  await renderPage();
  await openDialog("Copy agent");

  assert.ok(screen.getByRole("heading", { name: "Copy agent" }));
  assert.equal(writes("POST", "/api/agents").length, 0);
  assert.notEqual((screen.getByLabelText("Name") as HTMLInputElement).value, agent.name);

  await act(async () => {
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: " reviewer-copy " } });
  });
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Copy agent" }));
  });

  await waitFor(() => assert.equal(writes("POST", "/api/agents").length, 1));
  assert.equal(
    (writes("POST", "/api/agents")[0].body as Record<string, unknown>).name,
    "reviewer-copy",
  );
});

test("deleting an agent confirms first and then removes it", async () => {
  await renderPage();
  const deleteAction = document.querySelectorAll("tbody button")[4];
  await act(async () => {
    fireEvent.click(deleteAction);
  });
  await screen.findByRole("dialog");

  assert.match(
    screen.getByText(/Are you sure you want to delete agent/).textContent ?? "",
    /"reviewer"/,
  );
  assert.equal(writes("DELETE", `/api/agents/${agent.id}`).length, 0);

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
  });

  await waitFor(() => assert.equal(writes("DELETE", `/api/agents/${agent.id}`).length, 1));
});

test("toggling the enabled switch sends the new state", async () => {
  await renderPage();

  await act(async () => {
    fireEvent.click(screen.getByRole("switch", { name: "Reviewer enabled" }));
  });

  await waitFor(() => assert.equal(writes("PUT", "/api/agents/enabled").length, 1));
  assert.deepEqual(writes("PUT", "/api/agents/enabled")[0].body, {
    agentId: agent.id,
    enable: false,
  });
});
