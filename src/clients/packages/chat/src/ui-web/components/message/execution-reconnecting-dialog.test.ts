import assert from "node:assert/strict";
import test from "node:test";
import * as React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { ExecutionReconnectingDialog } from "./execution-reconnecting-dialog";

test("reconnecting dialog offers an immediate retry during the automatic wait", () => {
  const markup = renderToStaticMarkup(
    React.createElement(ExecutionReconnectingDialog, {
      state: { status: "reconnecting", retryAttempt: 6, retryDelayMs: 5_000 },
      onRetry: () => undefined,
    }),
  );

  assert.match(markup, /Trying again in 5 seconds/);
  assert.match(markup, />Retry now<\/button>/);
  assert.match(markup, /1\/5/);
});

test("reconnecting dialog disables duplicate retries while an attempt is running", () => {
  const markup = renderToStaticMarkup(
    React.createElement(ExecutionReconnectingDialog, {
      state: { status: "reconnecting", retryAttempt: 6, retryDelayMs: 0 },
      onRetry: () => undefined,
    }),
  );

  assert.match(markup, /<button[^>]*disabled=""[^>]*>/);
  assert.match(markup, />Retrying…<\/button>/);
});

test("the first five attempts render no dialog, status announcement or overlay", () => {
  for (let retryAttempt = 1; retryAttempt <= 5; retryAttempt += 1) {
    assert.equal(
      renderToStaticMarkup(
        React.createElement(ExecutionReconnectingDialog, {
          state: { status: "reconnecting", retryAttempt, retryDelayMs: 1_000 },
          onRetry: () => undefined,
        }),
      ),
      "",
    );
  }
});

test("visible retries show only five attempts and five progress segments", () => {
  for (let retryAttempt = 6; retryAttempt <= 10; retryAttempt += 1) {
    const markup = renderToStaticMarkup(
      React.createElement(ExecutionReconnectingDialog, {
        state: { status: "reconnecting", retryAttempt, retryDelayMs: 14_300 },
        onRetry: () => undefined,
      }),
    );
    assert.ok(markup.includes(`${retryAttempt - 5}/5`));
    assert.equal(markup.match(/h-1 flex-1/g)?.length, 5);
    assert.match(markup, /Trying again in 15 seconds/);
  }
});

test("exhausted retries retain manual retry and display five out of five", () => {
  const markup = renderToStaticMarkup(
    React.createElement(ExecutionReconnectingDialog, {
      state: { status: "failed", retryAttempt: 10, retryDelayMs: 0 },
      onRetry: () => undefined,
    }),
  );
  assert.match(markup, /5\/5/);
  assert.match(markup, /Failed to rejoin/);
  assert.match(markup, />Retry<\/button>/);
});
