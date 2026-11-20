import assert from "node:assert/strict";
import test, { type TestContext } from "node:test";

import { setupDomEnvironment, startApiServer, type ApiRequestRecord } from "@agw/test-harness";
import type {
  EventDeliveryResponse,
  EventResponse,
  EventScheduleResponse,
  EventSourceResponse,
  EventSubscriptionResponse,
  UnpublishedEventRecord,
} from "@agw/api";

const { React, act, fireEvent, render, screen, waitFor, within } = await setupDomEnvironment();
const { apiPost, configureApiRuntime, resetApiRuntime } = await import("@agw/api");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { EventsPage } = await import("./events-page.tsx");

const timestamp = "2026-11-14T00:00:00Z";
const events: EventResponse[] = Array.from({ length: 45 }, (_, index) => ({
  id: `event-${index + 1}`,
  sourceId: "source-1",
  chainId: "chain-1",
  receivedAt: timestamp,
  hops: 0,
  dispatchState: 0,
  matchedCount: 0,
  cloudEvent: {
    specversion: "1.0",
    id: `ce-${index + 1}`,
    source: "/paging",
    type: "paging.test",
    subject: `Event ${index + 1}`,
  },
}));
const deliveries: EventDeliveryResponse[] = events.slice(0, 2).flatMap((event) =>
  Array.from({ length: 45 }, (_, index) => ({
    id: `delivery-${event.id}-${index + 1}`,
    eventId: event.id,
    subscriptionId: "subscription-1",
    state: 0,
    attempt: 1,
  })),
);
const schedules: EventScheduleResponse[] = [
  { id: "schedule-1", summary: "Scheduled test", dueAt: timestamp, state: 0, occurrence: 0 },
];
const waitingRecords: UnpublishedEventRecord[] = [
  { entityId: "waiting-1", topic: "event.accepted", state: "Pending", recoverAfter: timestamp },
];
const sourceOptions = [
  {
    id: "built-in-execution",
    name: "agw.execution",
    kind: 4,
    managedBy: "agw",
    eventTypes: ["agw.execution.completed"],
  },
  {
    id: "job-timer",
    name: "agw.timer.job-1-20261114",
    kind: 0,
    managedBy: "job",
    eventTypes: ["job.timer.fired"],
  },
  ...Array.from({ length: 120 }, (_, index) => ({
    id: `option-${index + 1}`,
    name: `Source option ${index + 1}`,
    kind: 1,
    managedBy: "user",
    eventTypes: ["paging.test"],
  })),
];

function newSource(index: number): EventSourceResponse {
  return {
    id: `source-${index}`,
    name: `Source ${index}`,
    kind: 1,
    managedBy: "user",
    ceSource: `/sources/${index}`,
    config: {},
    eventTypes: ["paging.test"],
    enabled: true,
    hasSecret: false,
    version: 1,
  };
}

function newSubscription(index: number): EventSubscriptionResponse {
  return {
    id: `subscription-${index}`,
    name: `Subscription ${index}`,
    enabled: true,
    sourceIds: ["source-1"],
    eventTypes: ["paging.test"],
    targetKind: 0,
    managedBy: "user",
    version: 1,
  };
}

function page<T>(items: T[], query: URLSearchParams, eventStream = false) {
  const pageIndex = Number(query.get(eventStream ? "PageIndex" : "pageIndex") ?? 1);
  const pageSize = Number(query.get(eventStream ? "PageSize" : "pageSize") ?? 20);
  return {
    items: items.slice((pageIndex - 1) * pageSize, pageIndex * pageSize),
    total: String(items.length),
    pageIndex,
    pageSize,
  };
}

async function renderPage(
  context: TestContext,
  {
    sources = Array.from({ length: 45 }, (_, index) => newSource(index + 1)),
    subscriptions = Array.from({ length: 45 }, (_, index) => newSubscription(index + 1)),
  } = {},
) {
  const api = await startApiServer({
    "GET /api/events/list": ({ query }: ApiRequestRecord) => {
      const type = query.get("Type");
      const chainId = query.get("ChainId");
      return page(
        events.filter(
          (event) =>
            (!type || event.cloudEvent.type === type) && (!chainId || event.chainId === chainId),
        ),
        query,
        true,
      );
    },
    "GET /api/events/get": ({ query }: ApiRequestRecord) =>
      events.find((event) => event.id === query.get("id")),
    "GET /api/events/sources/list": ({ query }: ApiRequestRecord) => page(sources, query),
    "GET /api/events/sources/options": sourceOptions,
    "GET /api/events/sources/providers": [
      {
        extensionId: "extension-1",
        extensionName: "taskpilot",
        extensionDisplayName: "TaskPilot",
        providerId: "issues",
        eventTypes: ["taskpilot.issue.*"],
        configurations: [{ id: "main", name: "Main", description: "Primary workspace" }],
      },
    ],
    "GET /api/events/subscriptions/list": ({ query }: ApiRequestRecord) =>
      page(subscriptions, query),
    "GET /api/events/deliveries/list": ({ query }: ApiRequestRecord) => {
      const eventId = query.get("eventId");
      return page(
        deliveries.filter((delivery) => !eventId || delivery.eventId === eventId),
        query,
      );
    },
    "GET /api/events/schedules/list": ({ query }: ApiRequestRecord) => page(schedules, query),
    "GET /api/events/unpublished/list": ({ query }: ApiRequestRecord) =>
      page(waitingRecords, query),
    "GET /api/agents": [],
    "GET /api/agentflows": [],
    "GET /api/projects": [],
    "POST /api/events/sources/create": ({ body }: ApiRequestRecord) => {
      const request = body as { name: string; kind: number; config: unknown };
      sources.push({
        ...newSource(sources.length + 1),
        name: request.name,
        kind: request.kind,
        config: request.config,
      });
      return {
        id: "created-source",
        version: 1,
        secret: request.kind === 1 ? "one-time-secret" : null,
      };
    },
    "POST /api/events/subscriptions/create": ({ body }: ApiRequestRecord) => {
      const request = body as EventSubscriptionResponse;
      subscriptions.push({ ...newSubscription(subscriptions.length + 1), ...request });
      return subscriptions.at(-1);
    },
    "POST /api/events/sources/delete": ({ body }: ApiRequestRecord) => {
      const { id } = body as { id: string };
      sources = sources.filter((source) => source.id !== id);
      return null;
    },
  });
  configureApiRuntime({ baseUrl: api.baseUrl, token: null });
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { gcTime: 0 },
    },
  });
  const view = render(
    React.createElement(QueryClientProvider, { client }, React.createElement(EventsPage)),
  );
  context.after(async () => {
    view.unmount();
    client.clear();
    resetApiRuntime();
    await api.close();
  });

  return {
    baseUrl: api.baseUrl,
    lastQuery: (path: string) =>
      api.requests.findLast((request) => request.method === "GET" && request.path === path)?.query,
    lastPost: (path: string) =>
      api.requests.findLast((request) => request.method === "POST" && request.path === path)?.body,
  };
}

async function switchTab(name: string) {
  await act(async () => {
    fireEvent.mouseDown(screen.getByRole("tab", { name }), {
      button: 0,
      ctrlKey: false,
    });
  });
}

async function clickButton(name: string | RegExp) {
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name }));
  });
}

async function enterConditionValue(number: number, value: string) {
  const input = screen.getByLabelText(`Condition ${number} values`);
  await act(async () => {
    fireEvent.change(input, { target: { value } });
  });
  await act(async () => {
    fireEvent.keyDown(input, { key: "Enter" });
  });
}

async function nextPage() {
  await waitFor(() => {
    assert.equal(screen.getByLabelText("Next page").hasAttribute("disabled"), false);
  });
  await clickButton("Next page");
}

async function changePageSize(size: number) {
  await act(async () => {
    fireEvent.click(screen.getByLabelText("Rows per page"));
  });
  const option = await screen.findByRole("option", { name: String(size) });
  await act(async () => {
    fireEvent.click(option);
  });
}

test("job-managed event sources and subscriptions show their job ID and stay read only", async (context) => {
  const jobId = "job-123";
  const source: EventSourceResponse = {
    ...newSource(1),
    id: "job-timer",
    name: "agw.timer.job-1-20261114",
    kind: 0,
    managedBy: "job",
    managedKey: jobId,
    ceSource: "agw://jobs/job-123",
  };
  const subscription: EventSubscriptionResponse = {
    ...newSubscription(1),
    name: "Job timer subscription",
    sourceIds: [source.id],
    eventTypes: ["job.timer.fired"],
    managedBy: "job",
    managedKey: jobId,
  };
  await renderPage(context, { sources: [source], subscriptions: [subscription] });

  await switchTab("Sources");
  assert.equal(
    (await screen.findByText(`Managed by Job ${jobId}`)).textContent,
    `Managed by Job ${jobId}`,
  );
  assert.equal(screen.getByText("Read only").textContent, "Read only");
  assert.equal(screen.queryAllByRole("button", { name: "Test" }).length, 0);
  assert.equal(screen.queryAllByRole("button", { name: "Edit" }).length, 0);

  await switchTab("Subscriptions");
  assert.equal(
    (await screen.findByText(`Managed by Job ${jobId}`)).textContent,
    `Managed by Job ${jobId}`,
  );
  assert.equal(screen.getByText("Read only").textContent, "Read only");
  assert.equal(screen.queryAllByRole("button", { name: "Edit" }).length, 0);
  assert.equal(screen.queryAllByRole("button", { name: "Delete" }).length, 0);
  await clickButton("View");
  const dialog = await screen.findByRole("dialog");
  assert.match(dialog.textContent ?? "", /View subscription/);
  assert.match(dialog.textContent ?? "", /job\.timer\.fired/);
  assert.ok(await within(dialog).findByText(source.name));
  assert.equal(within(dialog).queryByRole("button", { name: "Save subscription" }), null);
});

test("the six lists request page 1 with 20 rows first", async (context) => {
  const { lastQuery } = await renderPage(context);
  await waitFor(() => {
    const query = lastQuery("/api/events/list");
    assert.equal(query?.get("PageIndex"), "1");
    assert.equal(query?.get("PageSize"), "20");
  });
  assert.equal((await screen.findByText("45 events")).textContent, "45 events");
  for (const [tab, path] of [
    ["Sources", "sources"],
    ["Subscriptions", "subscriptions"],
    ["Deliveries", "deliveries"],
    ["Schedules", "schedules"],
    ["Waiting records", "unpublished"],
  ]) {
    await switchTab(tab);
    await waitFor(() => {
      const query = lastQuery(`/api/events/${path}/list`);
      assert.equal(query?.get("pageIndex"), "1");
      assert.equal(query?.get("pageSize"), "20");
    });
    await screen.findByLabelText("Rows per page");
  }
});

test("choosing 100 rows requests page 1 and Next requests page 2", async (context) => {
  const sources = Array.from({ length: 230 }, (_, index) => newSource(index + 1));
  const { lastQuery } = await renderPage(context, { sources });
  await switchTab("Sources");
  await nextPage();
  await screen.findByText("Showing 21–40 of 230");
  await changePageSize(100);
  await waitFor(() => {
    const query = lastQuery("/api/events/sources/list");
    assert.equal(query?.get("pageIndex"), "1");
    assert.equal(query?.get("pageSize"), "100");
  });
  await screen.findByText("Showing 1–100 of 230");
  await nextPage();
  await waitFor(() => {
    const query = lastQuery("/api/events/sources/list");
    assert.equal(query?.get("pageIndex"), "2");
    assert.equal(query?.get("pageSize"), "100");
  });
  await screen.findByText("Source 101");
});

test("applying filters and showing a chain return the event stream to page 1", async (context) => {
  const { lastQuery } = await renderPage(context);
  await nextPage();
  await screen.findByText("Event 21");
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Event type"), {
      target: { value: "paging.test" },
    });
  });
  await clickButton("Apply filters");
  await waitFor(() => {
    const query = lastQuery("/api/events/list");
    assert.equal(query?.get("PageIndex"), "1");
    assert.equal(query?.get("Type"), "paging.test");
  });

  await nextPage();
  await screen.findByText("Event 21");
  await clickButton("Apply filters");
  await screen.findByText("Event 1");

  await nextPage();
  const event = await screen.findByText("Event 21");
  await act(async () => {
    fireEvent.click(event);
  });
  await screen.findByRole("button", { name: "Show chain" });
  await clickButton("Show chain");
  await waitFor(() => {
    const query = lastQuery("/api/events/list");
    assert.equal(query?.get("PageIndex"), "1");
    assert.equal(query?.get("ChainId"), "chain-1");
  });
});

test("changing the delivery event filter or selecting another event returns deliveries to page 1", async (context) => {
  const { lastQuery } = await renderPage(context);
  await switchTab("Deliveries");
  await nextPage();
  await screen.findByText("delivery-event-1-21");
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Filter by event ID"), { target: { value: "event-1" } });
  });
  await waitFor(() => {
    const query = lastQuery("/api/events/deliveries/list");
    assert.equal(query?.get("pageIndex"), "1");
    assert.equal(query?.get("eventId"), "event-1");
  });

  await nextPage();
  await screen.findByText("delivery-event-1-21");
  await switchTab("Events");
  const event = await screen.findByText("Event 2");
  await act(async () => {
    fireEvent.click(event);
  });
  await switchTab("Deliveries");
  await waitFor(() => {
    const query = lastQuery("/api/events/deliveries/list");
    assert.equal(query?.get("pageIndex"), "1");
    assert.equal(query?.get("eventId"), "event-2");
  });
  await screen.findByText("delivery-event-2-1");
});

test("a page past the last page moves back to the last page", async (context) => {
  const { lastQuery } = await renderPage(context);
  await switchTab("Sources");
  await nextPage();
  await nextPage();
  await screen.findByText("Source 45");

  for (let index = 41; index <= 45; index += 1) {
    await apiPost("/api/events/sources/delete", { body: { id: `source-${index}`, version: 1 } });
  }
  await clickButton("Refresh");
  await waitFor(() => assert.equal(lastQuery("/api/events/sources/list")?.get("pageIndex"), "2"));
  await screen.findByText("Page 2 of 2");
  await screen.findByText("Source 40");
});

test("the subscription dialog lists every source option", async (context) => {
  const { lastQuery } = await renderPage(context);
  await switchTab("Subscriptions");
  await screen.findByText("Subscription 1");
  assert.equal(lastQuery("/api/events/sources/options"), undefined);
  assert.equal(lastQuery("/api/events/sources/list"), undefined);
  await clickButton("Add subscription");
  const dialog = await screen.findByRole("dialog");
  const trigger = within(dialog).getByRole("combobox", { name: "Sources" });
  assert.equal(trigger.getAttribute("aria-expanded"), "false");
  await act(async () => {
    fireEvent.click(trigger);
  });
  const list = await screen.findByRole("listbox", { name: "Sources" });
  assert.equal(list.getAttribute("aria-multiselectable"), "true");
  const options = await within(list).findAllByRole("option");
  assert.deepEqual(
    options.map((option) => option.textContent),
    sourceOptions.map((option) => option.name),
  );
});

test("creating a Webhook shows its receive URL and one-time secret", async (context) => {
  const { baseUrl, lastPost } = await renderPage(context, { sources: [] });
  await switchTab("Sources");
  await clickButton("Add source");
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "demo-ticket" } });
    fireEvent.click(screen.getByLabelText("Fixed type"));
    fireEvent.change(screen.getByLabelText("Fixed event type"), {
      target: { value: "demo.ticket.created" },
    });
  });
  await clickButton("Save source");
  await waitFor(() =>
    assert.deepEqual(lastPost("/api/events/sources/create"), {
      name: "webhook.demo-ticket",
      kind: 1,
      config: { fixedType: "demo.ticket.created" },
      enabled: true,
    }),
  );
  assert.equal(
    (await screen.findByTestId("webhook-url")).textContent,
    `${baseUrl}/api/events/webhooks/receive?sourceId=created-source`,
  );
  assert.equal(screen.getByTestId("webhook-secret").textContent, "one-time-secret");
});

test("comma-separated source types keep a trailing separator while typing", async (context) => {
  await renderPage(context, { sources: [] });
  await switchTab("Sources");
  await clickButton("Add source");
  const input = screen.getByLabelText("Event types") as HTMLInputElement;
  await act(async () => {
    fireEvent.change(input, { target: { value: "demo.one," } });
  });
  assert.equal(input.value, "demo.one,");
  await act(async () => {
    fireEvent.change(input, { target: { value: "demo.one,demo.two" } });
  });
  assert.equal(input.value, "demo.one,demo.two");
});

test("condition values can be added and removed as tags", async (context) => {
  await renderPage(context, { subscriptions: [] });
  await switchTab("Subscriptions");
  await clickButton("Add subscription");
  await act(async () => {
    fireEvent.click(screen.getByText("Advanced settings"));
  });
  await clickButton("Add condition");
  const input = screen.getByLabelText("Condition 1 values") as HTMLInputElement;
  await enterConditionValue(1, "proj1");
  assert.ok(screen.getByRole("button", { name: "Remove value proj1 from condition 1" }));
  assert.equal(screen.queryByRole("img", { name: "OR" }), null);

  await enterConditionValue(1, "proj2");
  assert.ok(screen.getByRole("button", { name: "Remove value proj2 from condition 1" }));
  assert.ok(screen.getByRole("img", { name: "OR" }));
  assert.equal(input.value, "");

  await clickButton("Remove value proj1 from condition 1");
  assert.equal(screen.queryByRole("button", { name: "Remove value proj1 from condition 1" }), null);
  assert.equal(screen.queryByRole("img", { name: "OR" }), null);
  assert.equal(document.activeElement, input);
});

test("a user Timer source shows only supported actions", async (context) => {
  await renderPage(context, {
    sources: [{ ...newSource(1), kind: 0, name: "Timer source" }],
  });
  await switchTab("Sources");
  await screen.findByText("Timer source");
  assert.equal(screen.queryByRole("button", { name: "Edit" }), null);
  assert.equal(screen.queryByRole("button", { name: "Test" }), null);
  assert.equal(screen.queryByRole("button", { name: "Rotate secret" }), null);
  assert.ok(screen.getByRole("button", { name: "Disable" }));
});

test("provider selection fills the extension name and offers named configurations", async (context) => {
  await renderPage(context, { sources: [] });
  await switchTab("Sources");
  await clickButton("Add source");
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Source type"), { target: { value: "3" } });
  });
  await screen.findByRole("option", { name: "TaskPilot · issues" });
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Source provider"), {
      target: { value: "extension-1:issues" },
    });
  });
  assert.equal((screen.getByLabelText("Name") as HTMLInputElement).value, "taskpilot");
  await act(async () => {
    fireEvent.click(screen.getByText("Advanced settings"));
  });
  assert.ok(screen.getByRole("option", { name: "Main · Primary workspace" }));
});

test("subscription form sends selected sources, types and scope conditions", async (context) => {
  const { lastPost } = await renderPage(context, { subscriptions: [] });
  await switchTab("Subscriptions");
  await clickButton("Add subscription");
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Completion" } });
  });
  const trigger = screen.getByRole("combobox", { name: "Sources" });
  await act(async () => {
    fireEvent.click(trigger);
  });
  const list = await screen.findByRole("listbox", { name: "Sources" });
  const execution = await within(list).findByRole("option", { name: "agw.execution" });
  const timer = within(list).getByRole("option", { name: "agw.timer.job-1-20261114" });
  await act(async () => {
    fireEvent.click(execution);
  });
  await act(async () => {
    fireEvent.click(timer);
  });
  assert.equal(execution.getAttribute("aria-selected"), "true");
  assert.equal(timer.getAttribute("aria-selected"), "true");
  await act(async () => {
    fireEvent.keyDown(screen.getByLabelText("Search sources"), { key: "Escape" });
  });
  assert.equal(trigger.getAttribute("aria-expanded"), "false");
  assert.equal(trigger.textContent, "2 selected");
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Event types"), {
      target: { value: "agw.execution.completed" },
    });
  });
  await clickButton("Add type");
  await act(async () => {
    fireEvent.keyDown(screen.getByLabelText("Target type"), { key: "Enter" });
  });
  await act(async () => {
    fireEvent.click(screen.getByRole("option", { name: "Extension handler" }));
  });
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Handler extension ID"), {
      target: { value: "extension-1" },
    });
    fireEvent.change(screen.getByLabelText("Handler ID"), { target: { value: "handler" } });
    fireEvent.change(screen.getByLabelText("Handler configuration JSON"), {
      target: { value: "{" },
    });
    assert.equal(
      (screen.getByRole("button", { name: "Save subscription" }) as HTMLButtonElement).disabled,
      true,
    );
    fireEvent.change(screen.getByLabelText("Handler configuration JSON"), {
      target: { value: '{"mode":"demo"}' },
    });
    fireEvent.click(screen.getByText("Advanced settings"));
  });
  await clickButton("Add condition");
  assert.equal(screen.queryByRole("img", { name: "AND" }), null);
  await clickButton("Add condition");
  await clickButton("Add condition");
  assert.equal(screen.getAllByRole("img", { name: "AND" }).length, 2);
  await clickButton("Remove condition 3");
  assert.ok(screen.getByRole("img", { name: "AND" }));
  await enterConditionValue(1, "proj1");
  await enterConditionValue(1, "proj2");
  assert.ok(screen.getByRole("img", { name: "OR" }));
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Condition 2 field"), {
      target: { value: "subject" },
    });
    fireEvent.change(screen.getByLabelText("Condition 2 values"), {
      target: { value: "review" },
    });
  });
  await clickButton("Save subscription");
  await waitFor(() => assert.ok(lastPost("/api/events/subscriptions/create")));
  const request = lastPost("/api/events/subscriptions/create") as Record<string, unknown>;
  assert.deepEqual(request.sourceIds, ["built-in-execution", "job-timer"]);
  assert.deepEqual(request.eventTypes, ["agw.execution.completed"]);
  assert.deepEqual(request.handlerConfig, { mode: "demo" });
  assert.deepEqual(request.filter, {
    and: [{ in: [{ var: "scope" }, ["proj1", "proj2"]] }, { in: [{ var: "subject" }, ["review"]] }],
  });
});

test("unsupported subscription filters are shown read only and can be cleared", async (context) => {
  await renderPage(context, {
    subscriptions: [{ ...newSubscription(1), filter: { "==": [{ var: "type" }, "paging.test"] } }],
  });
  await switchTab("Subscriptions");
  await screen.findByText("Subscription 1");
  await clickButton("Edit");
  await act(async () => {
    fireEvent.click(screen.getByText("Advanced settings"));
  });
  assert.match(screen.getByRole("dialog").textContent ?? "", /Clear conditions/);
  assert.match(screen.getByRole("dialog").textContent ?? "", /paging\.test/);
  await clickButton("Clear conditions");
  assert.ok(screen.getByRole("button", { name: "Add condition" }));
});

test("switching tabs keeps each list's page", async (context) => {
  await renderPage(context);
  await switchTab("Sources");
  await screen.findByText("Source 1");
  await changePageSize(10);
  await nextPage();
  await screen.findByText("Source 11");
  await switchTab("Subscriptions");
  await nextPage();
  await nextPage();
  await screen.findByText("Subscription 41");
  await switchTab("Events");

  await switchTab("Sources");
  await screen.findByText("Page 2 of 5");
  await screen.findByText("Showing 11–20 of 45");
  await switchTab("Subscriptions");
  await screen.findByText("Page 3 of 3");
});
