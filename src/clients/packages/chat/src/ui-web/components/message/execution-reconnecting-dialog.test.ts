import assert from "node:assert/strict";
import test from "node:test";
import * as React from "react";
import { renderToStaticMarkup } from "react-dom/server";

import { ExecutionReconnectingDialog } from "./execution-reconnecting-dialog";

test("reconnecting dialog offers an immediate retry during the automatic wait", () => {
  const markup = renderToStaticMarkup(
    React.createElement(ExecutionReconnectingDialog, {
      state: { status: "reconnecting", retryAttempt: 3, retryDelayMs: 5_000 },
      onRetry: () => undefined,
    }),
  );

  assert.match(markup, /Trying again in 5 seconds/);
  assert.match(markup, />Retry now<\/button>/);
  assert.match(markup, /3\/7/);
});

test("reconnecting dialog disables duplicate retries while an attempt is running", () => {
  const markup = renderToStaticMarkup(
    React.createElement(ExecutionReconnectingDialog, {
      state: { status: "reconnecting", retryAttempt: 3, retryDelayMs: 0 },
      onRetry: () => undefined,
    }),
  );

  assert.match(markup, /<button[^>]*disabled=""[^>]*>/);
  assert.match(markup, />Retrying…<\/button>/);
});
