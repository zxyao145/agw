import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const { React, act, fireEvent, render, screen } = await setupDomEnvironment();
const { FloatingChatPanel } = await import("./floating-chat-panel.tsx");

test("the chat child remains mounted as its presentation mode changes", () => {
  let mounts = 0;
  let unmounts = 0;
  function Child() {
    React.useEffect(() => {
      mounts += 1;
      return () => {
        unmounts += 1;
      };
    }, []);
    return React.createElement("div", null, "shared conversation");
  }
  const child = React.createElement(Child);
  const view = render(
    React.createElement(FloatingChatPanel, { mode: "page", onClose: () => {}, children: child }),
  );
  const content = screen.getByText("shared conversation");
  assert.equal(mounts, 1);

  for (const mode of ["floating", "hidden", "page"] as const) {
    view.rerender(
      React.createElement(FloatingChatPanel, { mode, onClose: () => {}, children: child }),
    );
    assert.strictEqual(screen.getByText("shared conversation"), content);
  }
  assert.equal(mounts, 1);
  assert.equal(unmounts, 0);
});

test("floating mode has the overlay slot and Escape requests close", () => {
  let closes = 0;
  const view = render(
    React.createElement(FloatingChatPanel, {
      mode: "floating",
      onClose: () => {
        closes += 1;
      },
      children: React.createElement("div", null, "conversation"),
    }),
  );

  assert.equal(document.querySelector('[data-slot="floating-chat-panel"]') !== null, true);
  fireEvent.keyDown(document.querySelector('[data-slot="floating-chat-panel"]')!, {
    key: "Escape",
  });
  assert.equal(closes, 1);
  act(() => view.unmount());
});

test("floating mode focuses the chat textarea instead of the Agent selector", () => {
  const view = render(
    React.createElement(FloatingChatPanel, {
      mode: "page",
      onClose: () => {},
      children: React.createElement(
        "div",
        null,
        React.createElement("button", { role: "combobox" }, "Agent"),
        React.createElement("textarea", { role: "combobox" }),
      ),
    }),
  );

  view.rerender(
    React.createElement(FloatingChatPanel, {
      mode: "floating",
      onClose: () => {},
      children: React.createElement(
        "div",
        null,
        React.createElement("button", { role: "combobox" }, "Agent"),
        React.createElement("textarea", { role: "combobox" }),
      ),
    }),
  );
  assert.equal(document.activeElement, document.querySelector("textarea"));
  assert.equal(document.activeElement?.tagName, "TEXTAREA");
  act(() => view.unmount());
});

test("closing restores focus after the return button becomes visible", () => {
  let visibilityWhenFocused = "";
  function Harness() {
    const [mode, setMode] = React.useState<"floating" | "hidden">("floating");
    const buttonRef = React.useRef<HTMLButtonElement>(null);
    return React.createElement(
      React.Fragment,
      null,
      React.createElement(
        "button",
        {
          ref: buttonRef,
          style: { visibility: mode === "floating" ? "hidden" : "visible" },
          onFocus: (event: React.FocusEvent<HTMLButtonElement>) => {
            visibilityWhenFocused = event.currentTarget.style.visibility;
          },
        },
        "Open chat",
      ),
      React.createElement(FloatingChatPanel, {
        mode,
        onClose: () => setMode("hidden"),
        returnFocusRef: buttonRef,
        children: React.createElement("div", null, "conversation"),
      }),
    );
  }

  render(React.createElement(Harness));
  fireEvent.click(screen.getByRole("button", { name: "Close chat" }));
  assert.equal(document.activeElement, screen.getByRole("button", { name: "Open chat" }));
  assert.equal(visibilityWhenFocused, "visible");
});
