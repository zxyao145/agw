import assert from "node:assert/strict";
import test from "node:test";

import {
  ALTERNATE_SUBMIT_SHORTCUT,
  getComposerPlaceholder,
  getSteerRejectionMessage,
  getSubmitButtonLabel,
  getSubmitShortcutHint,
  resolveComposerKey,
  resolveComposerSubmit,
  type ComposerSubmitInput,
  type SteerAvailability,
} from "./composer-submit";

const supported: SteerAvailability = { supported: true };
const unsupported: SteerAvailability = {
  supported: false,
  reason: "Claude Code cannot be steered.",
};

const availabilities: SteerAvailability[] = [supported, unsupported];

function resolve(overrides: Partial<ComposerSubmitInput>) {
  return resolveComposerSubmit({
    isExecuting: true,
    hasContent: true,
    preferredMode: "queue",
    steer: supported,
    ...overrides,
  });
}

test("an idle composer has only the plain send", () => {
  for (const preferredMode of ["queue", "steer"] as const) {
    for (const hasContent of [false, true]) {
      const state = resolve({ isExecuting: false, hasContent, preferredMode });
      assert.equal(state.action, "send");
      assert.equal(state.alternateMode, null);
      assert.equal(state.alternateBlockedReason, null);
      assert.equal(state.fallbackNote, null);
    }
  }
});

test("a running turn with an empty input is the stop button whatever the preference", () => {
  for (const preferredMode of ["queue", "steer"] as const) {
    for (const availability of availabilities) {
      assert.equal(
        resolve({ hasContent: false, preferredMode, steer: availability }).action,
        "stop",
      );
    }
  }
});

test("a running turn with content and the Queue default queues and offers Steer", () => {
  const state = resolve({ preferredMode: "queue" });

  assert.deepEqual(state, {
    action: "split",
    primaryMode: "queue",
    alternateMode: "steer",
    alternateBlockedReason: null,
    fallbackNote: null,
  });
});

test("a running turn with content and the Steer default steers and offers Queue", () => {
  const state = resolve({ preferredMode: "steer" });

  assert.deepEqual(state, {
    action: "split",
    primaryMode: "steer",
    alternateMode: "queue",
    alternateBlockedReason: null,
    fallbackNote: null,
  });
});

test("an unsupported target disables Steer in the menu and keeps the Queue default quiet", () => {
  const state = resolve({ preferredMode: "queue", steer: unsupported });

  assert.equal(state.primaryMode, "queue");
  assert.equal(state.alternateMode, "steer");
  assert.equal(state.alternateBlockedReason, "Claude Code cannot be steered.");
  assert.equal(state.fallbackNote, null);
});

test("a Steer default on an unsupported target falls back to Queue and says why", () => {
  const state = resolve({ preferredMode: "steer", steer: unsupported });

  assert.equal(state.action, "split");
  assert.equal(state.primaryMode, "queue");
  assert.equal(state.alternateMode, "steer");
  assert.equal(state.alternateBlockedReason, "Claude Code cannot be steered.");
  assert.match(
    state.fallbackNote ?? "",
    /Steering is unavailable.*Claude Code cannot be steered\./u,
  );
});

test("Enter uses the primary mode and the alternate shortcut uses the other mode", () => {
  const queueDefault = resolve({ preferredMode: "queue" });
  const steerDefault = resolve({ preferredMode: "steer" });

  assert.deepEqual(resolveComposerKey(queueDefault, "primary"), { type: "submit", mode: "queue" });
  assert.deepEqual(resolveComposerKey(queueDefault, "alternate"), {
    type: "submit",
    mode: "steer",
  });
  assert.deepEqual(resolveComposerKey(steerDefault, "primary"), { type: "submit", mode: "steer" });
  assert.deepEqual(resolveComposerKey(steerDefault, "alternate"), {
    type: "submit",
    mode: "queue",
  });
});

test("the alternate shortcut on an unsupported target gives the reason and sends nothing", () => {
  for (const preferredMode of ["queue", "steer"] as const) {
    assert.deepEqual(
      resolveComposerKey(resolve({ preferredMode, steer: unsupported }), "alternate"),
      { type: "blocked", reason: "Claude Code cannot be steered." },
    );
  }
});

test("Enter on an unsupported target with a Steer default sends to the queue", () => {
  assert.deepEqual(
    resolveComposerKey(resolve({ preferredMode: "steer", steer: unsupported }), "primary"),
    {
      type: "submit",
      mode: "queue",
    },
  );
});

test("the alternate shortcut does nothing when idle or when the input is empty", () => {
  assert.deepEqual(resolveComposerKey(resolve({ isExecuting: false }), "alternate"), {
    type: "ignore",
  });
  assert.deepEqual(resolveComposerKey(resolve({ hasContent: false }), "alternate"), {
    type: "ignore",
  });
});

test("each mode has its own placeholder and button label", () => {
  assert.notEqual(getComposerPlaceholder("queue"), getComposerPlaceholder("steer"));
  assert.notEqual(getSubmitButtonLabel("queue"), getSubmitButtonLabel("steer"));
});

test("the hint row names the shortcut of both modes while a turn runs", () => {
  assert.equal(
    getSubmitShortcutHint(resolve({ preferredMode: "queue" })),
    `Enter to queue, ${ALTERNATE_SUBMIT_SHORTCUT} to steer`,
  );
  assert.equal(
    getSubmitShortcutHint(resolve({ preferredMode: "steer" })),
    `Enter to steer, ${ALTERNATE_SUBMIT_SHORTCUT} to queue`,
  );
  assert.equal(
    getSubmitShortcutHint(resolve({ hasContent: false })),
    `Enter to queue, ${ALTERNATE_SUBMIT_SHORTCUT} to steer`,
  );
  assert.equal(getSubmitShortcutHint(resolve({ isExecuting: false })), null);
});

test("a rejected Steer shows the server's explanation without the SignalR prefix and code", () => {
  assert.equal(
    getSteerRejectionMessage(
      new Error(
        "An unexpected error occurred invoking 'DispatchCommand' on the server. HubException: 4090028: Too many steering messages are waiting.",
      ),
    ),
    "The message was not sent to the running task. Too many steering messages are waiting.",
  );
  assert.equal(
    getSteerRejectionMessage(new Error("Connection disconnected.")),
    "The message was not sent to the running task. Connection disconnected.",
  );
});
