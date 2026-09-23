import assert from "node:assert/strict";
import test, { afterEach } from "node:test";
import { setupDomEnvironment, startApiServer } from "@agw/test-harness";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen, waitFor } = environment;
const { configureApiRuntime, resetApiRuntime } = await import("@agw/api");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { validateMemoryForm, UserMemoryPage } = await import("./user-memory-page.tsx");

const memory = {
  id: "memory-1",
  name: "Writing preferences",
  description: "Answer style",
  createTime: "2026-09-20T01:00:00Z",
  updateTime: "2026-09-21T01:00:00Z",
};

const api = await startApiServer({
  "GET /api/user-memories/paged": { items: [memory], total: 1, pageIndex: 1, pageSize: 20 },
  "GET /api/user-memories/detail": { ...memory, content: "# Concise\n" },
  "POST /api/user-memories": memory,
  "PUT /api/user-memories": memory,
  "DELETE /api/user-memories": true,
});
configureApiRuntime({ baseUrl: api.baseUrl, token: null });
test.after(() => resetApiRuntime());

let activeClient: InstanceType<typeof QueryClient> | null = null;
afterEach(() => {
  activeClient?.clear();
  activeClient = null;
  api.requests.length = 0;
});

function writes(method: string, path: string) {
  return api.requests.filter((request) => request.method === method && request.path === path);
}

async function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { gcTime: 0 } },
  });
  activeClient = client;
  render(React.createElement(QueryClientProvider, { client }, React.createElement(UserMemoryPage)));
  await screen.findByText("Writing preferences");
}

test("validateMemoryForm trims metadata and keeps content", () => {
  assert.deepEqual(
    validateMemoryForm({
      name: "  Preferences  ",
      description: "  Answer style  ",
      content: "# Concise\n",
    }),
    { name: "Preferences", description: "Answer style", content: "# Concise\n" },
  );
});

test("validateMemoryForm rejects empty names, content, and overlong descriptions", () => {
  assert.throws(
    () => validateMemoryForm({ name: " ", description: "", content: "content" }),
    /name is required/i,
  );
  assert.throws(
    () => validateMemoryForm({ name: "name", description: "", content: " \n " }),
    /content is required/i,
  );
  assert.throws(
    () => validateMemoryForm({ name: "name", description: "x".repeat(301), content: "content" }),
    /300 characters/i,
  );
});

test("the page lists user memories with their metadata", async () => {
  await renderPage();

  assert.ok(screen.getByRole("heading", { name: "User Memory", level: 1 }));
  assert.ok(screen.getByText("Answer style"));
  assert.ok(screen.getByText("Private · Database"));
});

test("adding a memory sends the trimmed form fields", async () => {
  await renderPage();

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Add Memory" }));
  });
  await screen.findByRole("dialog");

  await act(async () => {
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "  New memory  " } });
  });
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Description"), { target: { value: "Hint" } });
  });
  await act(async () => {
    fireEvent.change(screen.getByLabelText("Memory content"), { target: { value: "# Body\n" } });
  });
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Save Memory" }));
  });

  await waitFor(() => assert.equal(writes("POST", "/api/user-memories").length, 1));
  assert.deepEqual(writes("POST", "/api/user-memories")[0].body, {
    name: "New memory",
    description: "Hint",
    content: "# Body\n",
  });
});

test("the editor previews GFM without rendering raw HTML", async () => {
  await renderPage();
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Add Memory" }));
  });
  await screen.findByRole("dialog");

  await act(async () => {
    fireEvent.change(screen.getByLabelText("Memory content"), {
      target: { value: "**bold** <script>alert(1)</script> <img src=x onerror=alert(1)>" },
    });
  });
  await act(async () => {
    fireEvent.mouseDown(screen.getByRole("tab", { name: "Preview" }), {
      button: 0,
      ctrlKey: false,
    });
  });

  await waitFor(() => assert.ok(screen.getByText("bold")));
  assert.equal(document.querySelector("script"), null);
  assert.equal(document.querySelector("img"), null);
});

test("deleting a memory asks for confirmation before removing it", async () => {
  await renderPage();

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Delete Writing preferences" }));
  });
  await screen.findByRole("dialog");
  assert.ok(screen.getByText(/Delete "Writing preferences" permanently/));
  assert.equal(writes("DELETE", "/api/user-memories").length, 0);

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
  });

  await waitFor(() => assert.equal(writes("DELETE", "/api/user-memories").length, 1));
});

test("editing a memory loads its content only after the editor opens", async () => {
  await renderPage();
  assert.equal(
    api.requests.filter((request) => request.path === "/api/user-memories/detail").length,
    0,
  );

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Edit Writing preferences" }));
  });
  await screen.findByRole("dialog");

  await waitFor(() =>
    assert.equal(
      api.requests.filter((request) => request.path === "/api/user-memories/detail").length,
      1,
    ),
  );
  await waitFor(() => {
    assert.equal(
      (screen.getByLabelText("Memory content") as HTMLTextAreaElement).value,
      "# Concise\n",
    );
  });
});
