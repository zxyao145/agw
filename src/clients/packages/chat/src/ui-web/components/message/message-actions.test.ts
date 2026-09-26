import assert from "node:assert/strict";
import test from "node:test";
import { setupDomEnvironment } from "@agw/test-harness";

const environment = await setupDomEnvironment();
const { React, act, cleanup, fireEvent, render, screen } = environment;
const { navigator } = environment.window;
const { presentMessage } = await import("@agw/chat-core");
const { MessageActions } = await import("./message-actions");
const { toast } = await import("sonner");

function renderActions(
  role = "user",
  result = false,
  createdAt: string | null = "2026-09-20T13:38:00Z",
) {
  const message = presentMessage({
    messageId: "test",
    role,
    createdAt,
    additionalProperties: result ? { type: "result" } : undefined,
    contents: [{ type: "TextContent", content: "**bold**\n\n[link](https://example.com)" }],
  })!;
  return render(React.createElement(MessageActions, { message }));
}

test("message time uses local HH:mm and invalid or absent times are hidden", () => {
  const previousTZ = process.env.TZ;
  process.env.TZ = "Asia/Singapore";
  try {
    for (const [timestamp, expected] of [
      ["2026-09-20T13:38:00Z", "21:38"],
      ["2026-09-20T16:00:00Z", "00:00"],
      ["invalid", null],
      [null, null],
    ]) {
      const view = renderActions("user", false, timestamp);
      assert.equal(view.container.querySelector("time")?.textContent ?? null, expected);
      view.unmount();
    }
  } finally {
    if (previousTZ === undefined) delete process.env.TZ;
    else process.env.TZ = previousTZ;
  }
});

test("copy writes only Markdown body and resets success two seconds after the last copy", async (context) => {
  const copied: string[] = [];
  Object.defineProperty(navigator, "clipboard", {
    configurable: true,
    value: {
      writeText: async (text: string) => {
        copied.push(text);
      },
    },
  });
  renderActions("assistant", true);
  context.mock.timers.enable({ apis: ["setTimeout"] });
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Copy message" })));
  assert.deepEqual(copied, ["**bold**\n\n[link](https://example.com)"]);
  act(() => context.mock.timers.tick(1_500));
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Message copied" })));
  act(() => context.mock.timers.tick(1_500));
  assert.ok(screen.getByRole("button", { name: "Message copied" }));
  act(() => context.mock.timers.tick(500));
  assert.ok(screen.getByRole("button", { name: "Copy message" }));
  cleanup();
  context.mock.timers.reset();
});

test("clipboard failure reports an error without showing copied state", async (context) => {
  const errors: string[] = [];
  context.mock.method(toast, "error", (message: string) => {
    errors.push(message);
    return 1;
  });
  Object.defineProperty(navigator, "clipboard", {
    configurable: true,
    value: {
      writeText: async () => {
        throw new Error("denied");
      },
    },
  });
  renderActions();
  await act(async () => fireEvent.click(screen.getByRole("button", { name: "Copy message" })));
  assert.deepEqual(errors, ["Unable to copy message"]);
  assert.ok(screen.getByRole("button", { name: "Copy message" }));
});

test("image-only messages disable copying", () => {
  const message = presentMessage({
    messageId: "image",
    role: "user",
    contents: [{ type: "DataContent", uri: "data:image/png;base64,a", name: "image" }],
  })!;
  render(React.createElement(MessageActions, { message }));
  assert.equal(screen.getByRole("button", { name: "Copy message" }).hasAttribute("disabled"), true);
});
