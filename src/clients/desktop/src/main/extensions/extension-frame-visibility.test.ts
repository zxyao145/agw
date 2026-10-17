import assert from "node:assert/strict";
import test from "node:test";

import { FrameVisibility } from "./extension-frame-visibility";

function visibleFrame(): FrameVisibility {
  const frame = new FrameVisibility();
  assert.equal(frame.visible, false);
  assert.equal(frame.setLayoutVisible(true), true);
  return frame;
}

test("a completed capture after obscuring hides the frame", () => {
  const frame = visibleFrame();
  const token = frame.beginObscure();
  assert.equal(frame.completeObscure(token), true);
  assert.equal(frame.visible, false);
  assert.equal(frame.reveal(), true);
});

test("revealing before the capture finishes discards the snapshot and keeps the frame visible", () => {
  const frame = visibleFrame();
  const token = frame.beginObscure();
  assert.equal(frame.reveal(), true);
  assert.equal(frame.completeObscure(token), false);
  assert.equal(frame.visible, true);
});

test("only the latest of repeated obscure and reveal calls takes effect", () => {
  const frame = visibleFrame();
  const tokens: number[] = [];
  for (let index = 0; index < 5; index += 1) {
    tokens.push(frame.beginObscure());
    frame.reveal();
  }
  const last = frame.beginObscure();
  assert.deepEqual(
    tokens.map((token) => frame.completeObscure(token)),
    [false, false, false, false, false],
  );
  assert.equal(frame.completeObscure(last), true);
  frame.reveal();
  assert.equal(
    tokens.concat(last).some((token) => frame.completeObscure(token)),
    false,
  );
  assert.equal(frame.visible, true);
});

test("a layout hidden while obscured stays hidden after revealing", () => {
  const frame = visibleFrame();
  const token = frame.beginObscure();
  assert.equal(frame.setLayoutVisible(false), false);
  assert.equal(frame.completeObscure(token), true);
  assert.equal(frame.reveal(), false);
  assert.equal(frame.setLayoutVisible(true), true);
});

test("captures finishing after the frame is destroyed are discarded", () => {
  const frame = visibleFrame();
  const token = frame.beginObscure();
  frame.destroy();
  assert.equal(frame.completeObscure(token), false);
});
