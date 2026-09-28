import assert from "node:assert/strict";
import test from "node:test";
import { installLayoutMetrics, setupDomEnvironment } from "@agw/test-harness";
import type { ConversationRenderItem, PresentedContent } from "@agw/chat-core";

const environment = await setupDomEnvironment();
const { React, fireEvent, render, screen, waitFor, within } = environment;
installLayoutMetrics(environment.window);
const { Conversation } = await import("./conversation.tsx");

const png =
  "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jN1cAAAAASUVORK5CYII=";
const uri = "https://images.example.com/photo.webp";

function imageItem(content: PresentedContent): ConversationRenderItem {
  return {
    key: "image-message",
    type: "message",
    alignment: "left",
    width: "normal",
    message: {
      identity: "image-message",
      source: { messageId: "image-message", role: "assistant", contents: [] },
      alignment: "left",
      width: "normal",
      meta: null,
      contents: [content],
    },
  };
}

function Harness({
  content = { type: "image", uri: png, name: "photo.png" },
  conversationKey = "a",
  removed = false,
}: {
  content?: PresentedContent;
  conversationKey?: string;
  removed?: boolean;
}) {
  const scrollElementRef = React.useRef<HTMLDivElement>(null);
  const [ready, setReady] = React.useState(false);
  React.useLayoutEffect(() => setReady(true), []);
  return React.createElement(
    "div",
    { ref: scrollElementRef, tabIndex: -1, "data-testid": "scroller" },
    ready &&
      React.createElement(Conversation, {
        items: removed ? [] : [imageItem(content)],
        conversationKey,
        scrollElementRef,
      }),
  );
}

const entries: { name: string; content: PresentedContent; src: string }[] = [
  { name: "attachment", content: { type: "image", uri: png, name: "photo.png" }, src: png },
  { name: "URI", content: { type: "uri", uri, name: "photo.webp" }, src: uri },
  {
    name: "linked Markdown",
    content: {
      type: "markdown",
      markdown: `[![photo](${uri})](https://example.com/)`,
      sourceType: "TextContent",
    },
    src: uri,
  },
];

for (const entry of entries) {
  test(`${entry.name} images open their original source in a portal and restore focus`, async () => {
    const view = render(React.createElement(Harness, { content: entry.content }));
    const trigger = await screen.findByRole("button", { name: /Preview image:/ });
    const scroller = screen.getByTestId("scroller");
    scroller.scrollTop = 90;
    assert.equal(fireEvent.click(trigger), false);
    const dialog = await screen.findByRole("dialog", { name: "Image preview" });
    assert.equal(view.container.contains(dialog), false);
    assert.equal(within(dialog).getByRole("img").getAttribute("src"), entry.src);
    assert.equal(view.container.getAttribute("inert"), "");
    assert.equal(within(dialog).getByLabelText("Zoom level").textContent, "100%");
    scroller.scrollTop = 180;
    fireEvent.click(within(dialog).getByRole("button", { name: "Close" }));
    await waitFor(() => assert.ok(screen.queryByRole("dialog") === null));
    assert.ok(document.activeElement === trigger);
    assert.equal(scroller.scrollTop, 90);
    assert.equal(view.container.hasAttribute("inert"), false);
  });
}

test("keyboard opens the preview and Escape closes it", async () => {
  render(React.createElement(Harness));
  const trigger = await screen.findByRole("button", { name: /Preview image:/ });
  fireEvent.keyDown(trigger, { key: "Enter" });
  await screen.findByRole("dialog");
  fireEvent.keyDown(document.activeElement!, { key: "Escape", code: "Escape" });
  await waitFor(() => assert.ok(screen.queryByRole("dialog") === null));
  assert.ok(document.activeElement === trigger);
});

test("streaming updates and removal of the source row keep the selected image open", async () => {
  const view = render(React.createElement(Harness));
  fireEvent.click(await screen.findByRole("button", { name: /Preview image:/ }));
  const dialog = await screen.findByRole("dialog");
  const image = within(dialog).getByRole("img");
  view.rerender(React.createElement(Harness, { content: { type: "uri", uri, name: "next.webp" } }));
  assert.ok(within(dialog).getByRole("img") === image);
  assert.equal(image.getAttribute("src"), png);
  view.rerender(React.createElement(Harness, { removed: true }));
  assert.ok(screen.getByRole("dialog") === dialog);
  fireEvent.click(within(dialog).getByRole("button", { name: "Close" }));
  await waitFor(() => assert.ok(screen.queryByRole("dialog") === null));
  assert.ok(document.activeElement === screen.getByTestId("scroller"));
});

test("switching conversations closes the preview and releases the background", async () => {
  const view = render(React.createElement(Harness));
  fireEvent.click(await screen.findByRole("button", { name: /Preview image:/ }));
  await screen.findByRole("dialog");
  view.rerender(React.createElement(Harness, { conversationKey: "b" }));
  assert.ok(screen.queryByRole("dialog") === null);
  assert.equal(view.container.hasAttribute("inert"), false);
  view.rerender(React.createElement(Harness, { conversationKey: "a" }));
  assert.ok(screen.queryByRole("dialog") === null);
});

test("Web leaves the native image context menu available on thumbnails and previews", async () => {
  render(React.createElement(Harness));
  const trigger = await screen.findByRole("button", { name: /Preview image:/ });
  assert.equal(fireEvent.contextMenu(trigger), true);
  fireEvent.click(trigger);
  const dialog = await screen.findByRole("dialog");
  assert.equal(fireEvent.contextMenu(within(dialog).getByRole("img")), true);
});

test("image load failures are announced in the message and preview", async () => {
  render(React.createElement(Harness));
  const trigger = await screen.findByRole("button", { name: /Preview image:/ });
  fireEvent.error(trigger);
  assert.ok(screen.getByText("Unable to load image: photo.png"));
  fireEvent.click(trigger);
  const dialog = await screen.findByRole("dialog");
  fireEvent.error(within(dialog).getByRole("img"));
  assert.equal(within(dialog).getByRole("alert").textContent, "Unable to load image");
});
