import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const { React, act, fireEvent, render, screen, window } = await setupDomEnvironment();
const { submitModePreference, useSubmitModePreference } =
  await import("./submit-mode-preference.ts");

test("the default submission mode is queue until a preference is saved", () => {
  window.localStorage.clear();

  assert.equal(submitModePreference.get(), "queue");
  submitModePreference.set("steer");
  assert.equal(submitModePreference.get(), "steer");
  assert.equal(window.localStorage.getItem("agw:chat-submit-mode"), "steer");
  submitModePreference.set("queue");
  assert.equal(submitModePreference.get(), "queue");
});

test("an unknown stored value reads as queue", () => {
  window.localStorage.setItem("agw:chat-submit-mode", "interrupt");

  assert.equal(submitModePreference.get(), "queue");
});

test("every component using the preference follows a change", () => {
  window.localStorage.clear();
  function Reader({ label }: { label: string }) {
    const [mode, setMode] = useSubmitModePreference();
    return React.createElement(
      "button",
      { type: "button", onClick: () => setMode(mode === "queue" ? "steer" : "queue") },
      `${label}:${mode}`,
    );
  }
  render(
    React.createElement(
      React.Fragment,
      null,
      React.createElement(Reader, { label: "a" }),
      React.createElement(Reader, { label: "b" }),
    ),
  );

  assert.ok(screen.getByText("a:queue"));
  act(() => {
    fireEvent.click(screen.getByText("a:queue"));
  });

  assert.ok(screen.getByText("a:steer"));
  assert.ok(screen.getByText("b:steer"));
});
