import assert from "node:assert/strict";
import test from "node:test";
import { rawApiResponse, setupDomEnvironment, startApiServer } from "@agw/test-harness";

const { React, fireEvent, render, screen, waitFor } = await setupDomEnvironment();
const { configureApiRuntime, resetApiRuntime } = await import("@agw/api");
const { QueryClient, QueryClientProvider } = await import("@agw/components/query");
const { AcpRegistryPicker } = await import("./acp-registry-picker");

const api = await startApiServer({
  "GET /api/agents/acp-registry": rawApiResponse({
    status: 502,
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ code: 5020013, title: "The ACP registry could not be read." }),
  }),
});
configureApiRuntime({ baseUrl: api.baseUrl, token: null });
test.after(resetApiRuntime);

function Harness({ initial }: { initial: string }) {
  const [extra, setExtra] = React.useState(initial);
  return React.createElement(
    React.Fragment,
    null,
    React.createElement(AcpRegistryPicker, { extra, setExtra }),
    React.createElement("textarea", {
      "aria-label": "JSON object",
      value: extra,
      onChange: (event: React.ChangeEvent<HTMLTextAreaElement>) => setExtra(event.target.value),
    }),
  );
}

function renderPicker(initial: string) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 }, mutations: { gcTime: 0 } },
  });
  render(
    React.createElement(QueryClientProvider, { client }, React.createElement(Harness, { initial })),
  );
}

test("a failed registry request leaves Extra Settings editable and allows refreshing", async () => {
  renderPicker("{}");
  assert.match((await screen.findByRole("alert")).textContent ?? "", /registry could not be read/);

  const editor = screen.getByRole<HTMLTextAreaElement>("textbox", { name: "JSON object" });
  fireEvent.change(editor, { target: { value: '{"command":"node"}' } });
  assert.equal(editor.value, '{"command":"node"}');

  fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
  await waitFor(() => assert.ok(api.requests.some(({ query }) => query.get("refresh") === "true")));
});

test("invalid Extra Settings disables registry selection", () => {
  renderPicker("{");

  assert.ok(screen.getByRole("combobox", { name: "ACP Registry" }).hasAttribute("disabled"));
  assert.ok(screen.getByText("Fix the Extra Settings JSON before selecting an agent."));
});
