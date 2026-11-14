import assert from "node:assert/strict";
import test, { afterEach, before } from "node:test";

type DomEnvironment = Awaited<ReturnType<typeof import("@agw/test-harness").setupDomEnvironment>>;
let environment: DomEnvironment;
let JobsWorkspace: typeof import("./jobs-workspace").JobsWorkspace;
let query: typeof import("./components.test-helper");

before(async () => {
  const { setupDomEnvironment } = await import("@agw/test-harness");
  environment = await setupDomEnvironment();
  ({ JobsWorkspace } = await import("./jobs-workspace"));
  query = await import("./components.test-helper");
});

afterEach(() => environment.cleanup());

test("Jobs opens logs and returns to the list inside the workspace", () => {
  const client = new query.QueryClient({
    defaultOptions: { queries: { staleTime: Infinity, gcTime: 0 } },
  });
  const job = {
    id: "job-1",
    name: "Scheduled work",
    enabled: true,
    projectId: "project-1",
    targetKind: 0,
    agentId: "agent-1",
    instructions: "Run",
    schedule: { kind: "Interval", interval: "PT5M" },
    scheduleCompleted: false,
    dueAt: "2026-11-08T10:00:00Z",
    lastDelivery: null,
  };
  const jobTwo = { ...job, id: "job-2", name: "Second job" };
  const firstPage = Array.from({ length: 21 }, (_, index) => ({
    id: `run-${index}`,
    deliveryId: `delivery-first-${index}`,
    attempt: 1,
    startedAt: "2026-11-08T09:55:00Z",
    endedAt: "2026-11-08T09:55:01Z",
    result: 0,
    error: null,
    conversationId: `conversation-${index}`,
  }));
  const secondPage = [
    {
      id: "run-second-page",
      deliveryId: "delivery-second-page",
      attempt: 2,
      startedAt: "2026-11-08T09:55:00Z",
      endedAt: "2026-11-08T09:55:01Z",
      result: 2,
      error: "Failed",
      conversationId: null,
    },
  ];
  client.setQueryData(["jobs"], [job, jobTwo]);
  client.setQueryData(["projects"], [{ id: job.projectId, name: "Project" }]);
  client.setQueryData(["agents"], []);
  client.setQueryData(["agentflows"], []);
  client.setQueryData(["job", job.id], job);
  client.setQueryData(["job", jobTwo.id], jobTwo);
  client.setQueryData(["job-logs", job.id], firstPage);
  client.setQueryData(["job-logs", job.id, 20, 20], secondPage);
  client.setQueryData(
    ["job-logs", jobTwo.id],
    Array.from({ length: 20 }, (_, index) => ({
      id: `run-job-two-${index}`,
      deliveryId: `delivery-job-two-${index}`,
      attempt: 1,
      startedAt: "2026-11-08T09:55:00Z",
      endedAt: "2026-11-08T09:55:01Z",
      result: 0,
      error: null,
      conversationId: null,
    })),
  );
  const { createElement } = environment.React;
  const view = environment.render(
    createElement(
      query.QueryClientProvider,
      { client },
      createElement(JobsWorkspace, { onOpenChat: () => undefined }),
    ),
  );
  assert.ok(environment.screen.getByRole("heading", { name: "Jobs" }));
  environment.fireEvent.click(
    environment.screen.getByRole("button", { name: "View Scheduled work logs" }),
  );
  assert.ok(environment.screen.getByRole("heading", { name: "Job Logs" }));
  assert.ok(environment.screen.getByText("Page 1"));
  assert.equal(
    environment.screen.getAllByRole("link", { name: "Go to Chat" })[0]?.getAttribute("href"),
    "/desktop/chat?projectId=project-1&conversationId=conversation-0",
  );

  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Next" }));
  assert.ok(environment.screen.getByText("delivery-second-page"));
  assert.ok(environment.screen.getAllByText("Failed").length > 0);
  assert.ok(environment.screen.getByText("End of results"));

  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Back to Jobs" }));
  assert.ok(environment.screen.getByRole("heading", { name: "Jobs" }));
  assert.equal(environment.screen.queryByRole("heading", { name: "Job Logs" }), null);
  environment.fireEvent.click(
    environment.screen.getByRole("button", { name: "View Second job logs" }),
  );
  assert.ok(environment.screen.getByText("Page 1"));
  assert.ok(environment.screen.getByText("delivery-job-two-0"));
  assert.ok(environment.screen.getByText("End of results"));
  assert.equal(
    environment.screen.getByRole("button", { name: "Next" }).hasAttribute("disabled"),
    true,
  );
  assert.deepEqual(client.getQueryData(["job-logs", job.id]), firstPage);
  view.unmount();
  client.clear();
});
