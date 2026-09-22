import assert from "node:assert/strict";
import test, { afterEach } from "node:test";
import { setupDomEnvironment, startApiServer } from "@agw/test-harness";

const environment = await setupDomEnvironment();
const { React, act, fireEvent, render, screen, waitFor, window } = environment;
const { configureApiRuntime, resetApiRuntime } = await import("@agw/api");
const { default: SettingsPage } = await import("./page.tsx");

const api = await startApiServer({
  "GET /api/auth/session": { isAdmin: true },
  "GET /api/auth/tokens": [
    { id: "token-1", name: "MacBook", prefix: "agw_ab", createdAt: "2026-09-20T01:00:00Z" },
  ],
  "POST /api/auth/tokens": { token: "agw_secret_value" },
  "PUT /api/auth/password": true,
  "GET /api/auth/oidc/providers": [],
});
configureApiRuntime({ baseUrl: api.baseUrl, token: null });
test.after(() => resetApiRuntime());

afterEach(() => (api.requests.length = 0));

function writes(method: string, path: string) {
  return api.requests.filter((request) => request.method === method && request.path === path);
}

test("the page lists the issued client tokens", async () => {
  render(React.createElement(SettingsPage));

  assert.ok(await screen.findByRole("heading", { name: "Server access", level: 1 }));
  assert.ok(await screen.findByText("MacBook"));
  assert.ok(screen.getByText(/agw_ab… ·/));
});

test("the administrator password form appears only for an administrator session", async () => {
  render(React.createElement(SettingsPage));

  assert.ok(await screen.findByText("Administrator password"));
  assert.ok(screen.getByLabelText("Current password"));
  assert.ok(screen.getByLabelText("New password"));
});

test("the new password requires eight characters before it can be submitted", async () => {
  render(React.createElement(SettingsPage));
  await screen.findByText("Administrator password");

  const newPassword = screen.getByLabelText("New password");
  const submit = screen.getByRole("button", { name: "Change password" });

  assert.equal(newPassword.getAttribute("minlength"), "8");
  assert.equal(submit.hasAttribute("disabled"), true);

  await act(async () => {
    fireEvent.change(newPassword, { target: { value: "short" } });
  });
  assert.equal(submit.hasAttribute("disabled"), true);

  await act(async () => {
    fireEvent.change(newPassword, { target: { value: "long-enough-password" } });
  });
  assert.equal(submit.hasAttribute("disabled"), false);
});

test("changing the password sends both fields to the server", async () => {
  render(React.createElement(SettingsPage));
  await screen.findByText("Administrator password");

  await act(async () => {
    fireEvent.change(screen.getByLabelText("Current password"), { target: { value: "old" } });
  });
  await act(async () => {
    fireEvent.change(screen.getByLabelText("New password"), { target: { value: "new-password" } });
  });
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Change password" }));
  });

  await waitFor(() => assert.equal(writes("PUT", "/api/auth/password").length, 1));
  assert.deepEqual(writes("PUT", "/api/auth/password")[0].body, {
    currentPassword: "old",
    newPassword: "new-password",
  });
});

test("a created token offers both copy actions with a shared secret", async () => {
  render(React.createElement(SettingsPage));
  await screen.findByText("MacBook");

  await act(async () => {
    fireEvent.change(screen.getByLabelText("Token name"), { target: { value: "Test client" } });
  });
  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Create" }));
  });

  await screen.findByText("Copy this secret now. It will not be shown again.");
  assert.ok(screen.getByText("agw_secret_value"));

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Copy token" }));
  });
  assert.equal(await window.navigator.clipboard.readText(), "agw_secret_value");

  await act(async () => {
    fireEvent.click(screen.getByRole("button", { name: "Copy config" }));
  });
  const copied = await window.navigator.clipboard.readText();
  const decoded = Buffer.from(
    copied.replaceAll("-", "+").replaceAll("_", "/"),
    "base64",
  ).toString();
  const config = JSON.parse(decoded);
  assert.equal(config.version, 2);
  assert.equal(config.token, "agw_secret_value");
  assert.equal(config.apiMajorVersion, 1);
});
