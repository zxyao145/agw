import assert from "node:assert/strict";
import test from "node:test";

import { setupDomEnvironment } from "@agw/test-harness";
import type { EventSourceResponse, EventSubscriptionResponse } from "@agw/api";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen } = environment;
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { EventsPage } = await import("./events-page.tsx");

test("job-managed event sources and subscriptions show their job ID and stay read only", async () => {
  const jobId = "job-123";
  const source: EventSourceResponse = {
    id: "source-1",
    name: "Job timer",
    kind: 0,
    managedBy: "job",
    managedKey: jobId,
    ceSource: "agw://jobs/job-123",
    config: {},
    enabled: true,
    hasSecret: false,
    version: 1,
  };
  const subscription: EventSubscriptionResponse = {
    id: "subscription-1",
    name: "Job timer subscription",
    enabled: true,
    sourceIds: [source.id],
    publisherKeys: [],
    eventTypes: ["job.timer.fired"],
    targetKind: 0,
    managedBy: "job",
    managedKey: jobId,
    version: 1,
  };
  const client = new QueryClient({
    defaultOptions: { queries: { staleTime: Infinity, gcTime: 0 } },
  });
  client.setQueryData(["events", "list", {}], []);
  client.setQueryData(["events", "sources"], [source]);
  client.setQueryData(["events", "subscriptions"], [subscription]);

  const view = render(
    React.createElement(QueryClientProvider, { client }, React.createElement(EventsPage)),
  );

  await screen.findByRole("heading", { name: "Events", level: 1 });
  await act(async () => {
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Sources" }), {
      button: 0,
      ctrlKey: false,
    });
  });
  assert.ok(screen.getByText(`Managed by Job ${jobId}`));
  assert.ok(screen.getByText("Read only"));
  assert.equal(screen.queryByRole("button", { name: "Test" }), null);
  assert.equal(screen.queryByRole("button", { name: "Edit" }), null);

  await act(async () => {
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Subscriptions" }), {
      button: 0,
      ctrlKey: false,
    });
  });
  assert.ok(screen.getByText(`Managed by Job ${jobId}`));
  assert.ok(screen.getByText("Read only"));
  assert.equal(screen.queryByRole("button", { name: "Edit" }), null);
  assert.equal(screen.queryByRole("button", { name: "Delete" }), null);

  view.unmount();
  client.clear();
});
