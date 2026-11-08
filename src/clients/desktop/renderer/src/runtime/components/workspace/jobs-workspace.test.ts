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
    defaultOptions: { queries: { staleTime: Infinity } },
  });
  const job = {
    id: "job-1",
    projectId: "project-1",
    agentType: null,
    agentId: null,
    name: "Scheduled work",
    prompt: null,
    triggerType: 1,
    triggerValue: "00:05:00",
    nextRunTime: "2026-11-08T10:00:00Z",
    status: 0,
    isEnabled: true,
    retryCount: 0,
    maxRetryCount: 3,
    lastError: null,
  };
  client.setQueryData(["jobs"], [job]);
  client.setQueryData(["projects"], [{ id: job.projectId, name: "Project" }]);
  client.setQueryData(["agents"], []);
  client.setQueryData(["agentflows"], []);
  client.setQueryData(["job", job.id], job);
  client.setQueryData(
    ["job-logs", job.id],
    [
      {
        id: "log-1",
        jobId: job.id,
        conversationId: "conversation-1",
        startTime: "2026-11-08T09:55:00Z",
        endTime: "2026-11-08T09:55:01Z",
        success: true,
        attempt: 1,
        errorMessage: null,
      },
    ],
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
  assert.equal(
    environment.screen.getByRole("link", { name: "Go to Chat" }).getAttribute("href"),
    "/desktop/chat?projectId=project-1&conversationId=conversation-1",
  );
  environment.fireEvent.click(environment.screen.getByRole("button", { name: "Back to Jobs" }));
  assert.ok(environment.screen.getByRole("heading", { name: "Jobs" }));
  assert.equal(environment.screen.queryByRole("heading", { name: "Job Logs" }), null);
  view.unmount();
  client.clear();
});
