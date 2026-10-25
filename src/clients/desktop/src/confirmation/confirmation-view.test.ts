import assert from "node:assert/strict";
import test, { before } from "node:test";

import type { ConfirmationAnswer, ConfirmationView } from "../shared/contracts";

// 模块加载时建立 DOM 环境，关闭 window 的 after 钩子属于整个文件。The DOM environment is set up at module load, so the
// after hook that closes the window belongs to the file.
const environmentReady = import("@agw/test-harness").then(({ setupDomEnvironment }) =>
  setupDomEnvironment(),
);

let renderConfirmation: typeof import("./confirmation-view").renderConfirmation;

before(async () => {
  await environmentReady;
  ({ renderConfirmation } = await import("./confirmation-view"));
});

function render(view: ConfirmationView) {
  const container = document.createElement("main");
  document.body.replaceChildren(container);
  const answers: ConfirmationAnswer[] = [];
  const result = renderConfirmation(container, view, (answer) => answers.push(answer));
  const buttons = [...container.querySelectorAll("footer button")] as HTMLButtonElement[];
  return { container, answers, result, buttons };
}

test("extension text renders as text, and the message box answers with button indexes", () => {
  const markup = '<img src="x" onerror="alert(1)"><b>bold</b>';
  const { container, answers, result, buttons } = render({
    kind: "message",
    extensionId: "sample",
    extensionName: `Sample ${markup}`,
    type: "warning",
    message: markup,
    detail: "Line 1\nLine 2",
    buttons: [`Save ${markup}`, "Discard", "Cancel"],
    defaultId: 1,
    cancelId: 2,
  });
  assert.equal(container.querySelector("img, b"), null);
  assert.equal(container.querySelector("h1")?.textContent, `Sample ${markup}`);
  assert.equal(
    container.querySelector(".source")?.textContent,
    `Message from Sample ${markup} (sample)`,
  );
  assert.equal(container.querySelector(".message")?.textContent, markup);
  assert.equal(container.querySelector(".message-type")?.textContent, "Warning");
  assert.deepEqual(
    buttons.map((button) => [button.textContent, button.className]),
    [
      [`Save ${markup}`, "plain"],
      ["Discard", "primary"],
      ["Cancel", "plain"],
    ],
  );
  assert.equal(result.cancel, 2);
  result.focus();
  assert.equal(document.activeElement, buttons[1]);
  buttons[0].click();
  assert.deepEqual(answers, [0]);
  assert.ok(buttons.every((button) => button.disabled));
});

test("permission approvals show the app's descriptions, the source and the app's buttons", () => {
  const { container, answers, result, buttons } = render({
    kind: "install",
    extensionId: "sample",
    extensionName: "Sample",
    version: "1.0.0",
    installedVersion: "2.0.0",
    downgrade: true,
    permissions: [
      {
        name: "fs.local.read",
        description: "Read any file or folder on this computer.",
        highRisk: true,
      },
    ],
    hostPermissions: [{ pattern: "http://intranet.example.com", unencrypted: true }],
    sendsData: true,
    runsAgents: false,
  });
  assert.equal(container.querySelector("h1")?.textContent, "Install Sample?");
  assert.equal(
    container.querySelector(".source")?.textContent,
    "Sample (sample) · Version 1.0.0 · Replaces version 2.0.0",
  );
  const permission = container.querySelector('[data-permission="fs.local.read"]');
  assert.equal(permission?.className, "high-risk");
  assert.equal(
    permission?.textContent,
    "fs.local.read — Read any file or folder on this computer.",
  );
  assert.match(
    container.textContent ?? "",
    /http:\/\/intranet\.example\.com Unencrypted connection/u,
  );
  assert.match(container.textContent ?? "", /older version than the one installed/u);
  assert.match(container.textContent ?? "", /send the data it reads/u);
  assert.deepEqual(
    buttons.map((button) => button.textContent),
    ["Cancel", "Replace"],
  );
  assert.equal(result.cancel, false);
  buttons[1].click();
  assert.deepEqual(answers, [true]);
});

test("links and unsaved changes name their source and answer true or false", () => {
  const link = render({
    kind: "open-link",
    extensionId: "sample",
    extensionName: "Sample",
    url: "https://example.com/docs",
  });
  assert.equal(link.container.querySelector(".url")?.textContent, "https://example.com/docs");
  assert.equal(
    link.container.querySelector(".source")?.textContent,
    "Requested by Sample (sample)",
  );
  link.buttons[0].click();
  assert.deepEqual(link.answers, [false]);

  const unsaved = render({
    kind: "discard-unsaved",
    items: [
      {
        fileName: "notes.md",
        viewerTitle: "Mixed",
        extensionId: "text-editor",
        extensionName: "Text Editor",
      },
    ],
  });
  assert.equal(
    unsaved.container.querySelector("li")?.textContent,
    "notes.md · Mixed (Text Editor, text-editor)",
  );
  assert.deepEqual(
    unsaved.buttons.map((button) => [button.textContent, button.className]),
    [
      ["Cancel", "plain"],
      ["Discard", "danger"],
    ],
  );
  unsaved.buttons[1].click();
  assert.deepEqual(unsaved.answers, [true]);

  const enable = render({
    kind: "enable",
    extensionId: "sample",
    extensionName: "Sample",
    version: "1.0.0",
    installedVersion: null,
    downgrade: false,
    permissions: [],
    hostPermissions: [],
    sendsData: false,
    runsAgents: false,
  });
  assert.match(enable.container.textContent ?? "", /requests no permissions/u);
  assert.deepEqual(
    enable.buttons.map((button) => button.textContent),
    ["Cancel", "Enable"],
  );
});
