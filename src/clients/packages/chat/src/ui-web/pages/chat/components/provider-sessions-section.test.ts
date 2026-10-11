import assert from "node:assert/strict";
import test, { afterEach } from "node:test";
import { setupDomEnvironment, startApiServer, type ApiRequestRecord } from "@agw/test-harness";

import type { ProviderSessionsSectionProps } from "./provider-sessions-section";

const { React, act, fireEvent, render, screen, waitFor, within, cleanup } =
  await setupDomEnvironment();
const { configureApiRuntime, resetApiRuntime } = await import("@agw/api");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { formatFriendlyLocalDateTime2 } = await import("@agw/components");
const { ProviderSessionsSection } = await import("./provider-sessions-section.tsx");

const PROJECT_ID = "019a1234-5678-7000-8000-000000000001";
const CONVERSATION_ID = "019a1234-5678-7000-8000-000000000002";
const OTHER_CONVERSATION_ID = "019a1234-5678-7000-8000-000000000003";
const CODEX_AGENT = "019a1234-5678-7000-8000-00000000000a";
const PI_AGENT = "019a1234-5678-7000-8000-00000000000b";
const ROUTE = "/api/projects/conversations/provider-sessions";

type SessionRecord = {
  id: string;
  agentId: string;
  externalAgentName: string;
  providerSessionId: string;
  isActive: boolean;
  createTime: string;
  updateTime: string | null;
};

function createRecords(): SessionRecord[] {
  return [
    {
      id: "binding-codex-2",
      agentId: CODEX_AGENT,
      externalAgentName: "codex",
      providerSessionId: "22222222-2222-2222-2222-222222222222",
      isActive: true,
      createTime: "2026-10-09T03:00:00Z",
      updateTime: null,
    },
    {
      id: "binding-codex-1",
      agentId: CODEX_AGENT,
      externalAgentName: "codex",
      providerSessionId: "11111111-1111-1111-1111-111111111111",
      isActive: false,
      createTime: "2026-10-09T02:00:00Z",
      updateTime: "2026-10-09T03:00:00Z",
    },
    {
      id: "binding-pi-1",
      agentId: PI_AGENT,
      externalAgentName: "pi",
      providerSessionId: "33333333-3333-3333-3333-333333333333",
      isActive: false,
      createTime: "2026-10-09T01:00:00Z",
      updateTime: "2026-10-09T01:30:00Z",
    },
  ];
}

let records = createRecords();
const api = await startApiServer({
  [`GET ${ROUTE}`]: (request: ApiRequestRecord) =>
    request.query.get("conversationId") === CONVERSATION_ID ? records : [],
  [`POST ${ROUTE}/archive`]: (request: ApiRequestRecord) => {
    const { bindingId } = request.body as { bindingId: string };
    records = records.map((record) =>
      record.id === bindingId
        ? { ...record, isActive: false, updateTime: "2026-10-09T04:00:00Z" }
        : record,
    );
    return null;
  },
});
// 没有任何路由的服务端：请求都返回 404，用来驱动加载与归档的失败路径。
// A server without routes: every request returns 404, driving the load and archive failure paths.
const failingApi = await startApiServer({});
const readOnlyApi = await startApiServer({ [`GET ${ROUTE}`]: () => records });
configureApiRuntime({ baseUrl: api.baseUrl, token: null });
test.after(() => resetApiRuntime());

afterEach(() => {
  cleanup();
  records = createRecords();
  api.requests.length = 0;
  configureApiRuntime({ baseUrl: api.baseUrl, token: null });
});

function createElement(
  props: Partial<ProviderSessionsSectionProps>,
  client: InstanceType<typeof QueryClient>,
) {
  return React.createElement(
    QueryClientProvider,
    { client },
    React.createElement(ProviderSessionsSection, {
      serverId: "server-1",
      projectId: PROJECT_ID,
      conversationId: CONVERSATION_ID,
      conversationRunning: false,
      ...props,
    }),
  );
}

function renderSection(props: Partial<ProviderSessionsSectionProps> = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  const view = render(createElement(props, client));
  return {
    ...view,
    rerenderWith: (next: Partial<ProviderSessionsSectionProps>) =>
      view.rerender(createElement(next, client)),
  };
}

function providerSessionRequests() {
  return api.requests.filter((request) => request.path.startsWith(ROUTE));
}

// 断言失败时 node:assert 会展开整个 jsdom 元素，耗时极长；只比较元素是否存在。
// A failing node:assert expands the whole jsdom element, which takes extremely long; only presence is compared.
function isPresent(element: Element | null): boolean {
  return element !== null;
}

test("groups render the active session, archived groups and expandable history", async () => {
  renderSection();

  const codexArchive = await screen.findByRole("button", {
    name: "Archive the codex engine session",
  });
  assert.equal(codexArchive.hasAttribute("disabled"), false);
  assert.ok(screen.getByText("22222222-2222-2222-2222-222222222222"));
  assert.ok(screen.getByText("2 agents"));
  assert.equal(
    isPresent(screen.queryByRole("button", { name: "Archive the pi engine session" })),
    false,
  );
  assert.ok(screen.getByText("No active session"));
  assert.equal(isPresent(screen.queryByText("11111111-1111-1111-1111-111111111111")), false);

  await act(async () => {
    fireEvent.click(screen.getAllByRole("button", { name: "History (1)" })[0]);
  });

  // 历史记录一行只展示 session ID 和创建时间。
  // A history row shows only the session ID and the creation time.
  assert.equal(
    screen.getByText("11111111-1111-1111-1111-111111111111").closest("li")?.textContent,
    `11111111-1111-1111-1111-111111111111${formatFriendlyLocalDateTime2("2026-10-09T02:00:00Z")}`,
  );
  assert.deepEqual(
    providerSessionRequests().map((request) => [request.method, request.query.get("projectId")]),
    [["GET", PROJECT_ID]],
  );
});

test("archiving submits the record id and refreshes the group", async () => {
  renderSection();
  const archive = await screen.findByRole("button", { name: "Archive the codex engine session" });

  await act(async () => {
    fireEvent.click(archive);
  });

  // 请求返回之前按钮保持禁用并显示进行中状态。
  // Until the request returns the button stays disabled and shows the in-progress state.
  assert.equal(archive.hasAttribute("disabled"), true);
  assert.equal(archive.textContent, "Archiving…");
  await waitFor(() =>
    assert.equal(
      isPresent(screen.queryByRole("button", { name: "Archive the codex engine session" })),
      false,
    ),
  );
  const archiveRequest = providerSessionRequests().find((request) => request.method === "POST");
  assert.deepEqual(archiveRequest?.body, {
    projectId: PROJECT_ID,
    conversationId: CONVERSATION_ID,
    bindingId: "binding-codex-2",
  });
  assert.equal(screen.getAllByText("No active session").length, 2);
  assert.ok(screen.getAllByRole("button", { name: "History (2)" }));
  assert.equal(providerSessionRequests().filter((request) => request.method === "GET").length, 2);
});

test("a running conversation disables archiving until the run ends, then refreshes", async () => {
  const view = renderSection({ conversationRunning: true });

  const archive = await screen.findByRole("button", { name: "Archive the codex engine session" });
  assert.equal(archive.hasAttribute("disabled"), true);
  assert.ok(screen.getByText("Archiving is available after the current run finishes."));

  records = [
    { ...records[0], isActive: false, updateTime: "2026-10-09T05:00:00Z" },
    {
      ...records[0],
      id: "binding-codex-3",
      providerSessionId: "44444444-4444-4444-4444-444444444444",
      createTime: "2026-10-09T05:00:00Z",
    },
    ...records.slice(1),
  ];
  await act(async () => {
    view.rerenderWith({ conversationRunning: false });
  });

  await waitFor(() => assert.ok(screen.getByText("44444444-4444-4444-4444-444444444444")));
  assert.equal(
    screen
      .getByRole("button", { name: "Archive the codex engine session" })
      .hasAttribute("disabled"),
    false,
  );
});

test("a failed archive keeps the current content and re-enables the button", async () => {
  configureApiRuntime({ baseUrl: readOnlyApi.baseUrl, token: null });
  renderSection();
  const archive = await screen.findByRole("button", { name: "Archive the codex engine session" });

  await act(async () => {
    fireEvent.click(archive);
  });

  await waitFor(() =>
    assert.equal(
      screen
        .getByRole("button", { name: "Archive the codex engine session" })
        .hasAttribute("disabled"),
      false,
    ),
  );
  assert.ok(screen.getByText("22222222-2222-2222-2222-222222222222"));
});

test("a load failure shows the error with a retry entry", async () => {
  configureApiRuntime({ baseUrl: failingApi.baseUrl, token: null });
  renderSection();

  const retry = await screen.findByRole("button", { name: "Retry" });
  assert.ok(screen.getByText(/^Couldn.t load engine sessions:/));

  configureApiRuntime({ baseUrl: api.baseUrl, token: null });
  await act(async () => {
    fireEvent.click(retry);
  });

  await screen.findByText("22222222-2222-2222-2222-222222222222");
});

test("a conversation without bindings shows the empty state", async () => {
  renderSection({ conversationId: OTHER_CONVERSATION_ID });

  await screen.findByText(/^No engine sessions yet\./);
  assert.equal(isPresent(screen.queryByRole("button", { name: /^Archive/ })), false);
});

test("switching conversations loads the new conversation's sessions", async () => {
  const view = renderSection({ conversationId: OTHER_CONVERSATION_ID });
  await screen.findByText(/^No engine sessions yet\./);

  await act(async () => {
    view.rerenderWith({ conversationId: CONVERSATION_ID });
  });

  const section = await screen.findByRole("region", { name: "Engine Sessions" });
  await within(section).findByText("22222222-2222-2222-2222-222222222222");
  assert.deepEqual(
    providerSessionRequests().map((request) => request.query.get("conversationId")),
    [OTHER_CONVERSATION_ID, CONVERSATION_ID],
  );
});
