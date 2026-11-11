import assert from "node:assert/strict";
import { test } from "node:test";
import * as Lark from "@larksuiteoapi/node-sdk";
import { createCallbackChannel, createEventDispatcher } from "./feishu-event-dispatcher.mjs";

test("card.action.trigger returns the callback response through the official dispatcher", async () => {
  const payload = {
    schema: "2.0",
    header: {
      event_id: "evt-1",
      event_type: "card.action.trigger",
      app_id: "cli_test",
    },
    event: {
      operator: { open_id: "ou_test" },
      context: { open_message_id: "om_test" },
      action: {
        value: {
          notificationId: "019a4c17-5f00-7a80-8b3d-2b732f48f112",
          dispatchKey: "dispatch-1",
          option: 1,
        },
      },
    },
  };
  const callbackResponse = {
    state: "Processing",
    message: "Received. Processing the answer.",
    card: { schema: "2.0" },
    toast: { type: "success", content: "Received. Processing." },
  };
  const requests = [];
  const callbackChannel = createCallbackChannel((request) => requests.push(request));
  const dispatcher = createEventDispatcher({
    appRecordId: "019a4c17-5f00-7a80-8b3d-2b732f48f113",
    dispatcherOptions: { loggerLevel: Lark.LoggerLevel.error },
    sendEvent: () => {},
    requestCardAction: callbackChannel.request,
  });

  const responsePromise = dispatcher.invoke(payload, { needCheck: false });
  const callback = requests[0];
  assert.equal(callback.type, "callback");
  assert.equal(callback.appRecordId, "019a4c17-5f00-7a80-8b3d-2b732f48f113");
  assert.equal(callback.eventKey, "card.action.trigger");
  assert.equal(callback.payload.event_type, "card.action.trigger");
  assert.equal(callback.payload.event_id, "evt-1");
  assert.equal(callback.payload.app_id, "cli_test");
  assert.equal(callback.payload.operator.open_id, "ou_test");
  assert.equal(callback.payload.context.open_message_id, "om_test");
  assert.equal(callback.payload.action.value.option, 1);
  assert.match(callback.requestId, /^[0-9a-f-]{36}$/u);
  callbackChannel.resolve({ requestId: callback.requestId, payload: callbackResponse });
  const response = await responsePromise;

  assert.deepEqual(response, { card: callbackResponse.card, toast: callbackResponse.toast });
});

test("card.action.trigger returns a deadline toast and discards a late reply", async () => {
  const requests = [];
  const callbackChannel = createCallbackChannel((request) => requests.push(request));
  const dispatcher = createEventDispatcher({
    appRecordId: "019a4c17-5f00-7a80-8b3d-2b732f48f113",
    dispatcherOptions: { loggerLevel: Lark.LoggerLevel.error },
    sendEvent: () => {},
    requestCardAction: callbackChannel.request,
  });
  const response = await dispatcher.invoke(
    {
      schema: "2.0",
      header: { event_id: "evt-timeout", event_type: "card.action.trigger", app_id: "cli_test" },
      event: {
        operator: { open_id: "ou_test" },
        context: { open_message_id: "om_test" },
        action: { value: {} },
      },
    },
    { needCheck: false },
  );

  assert.equal(response.toast.type, "error");
  assert.equal(response.toast.content, "Could not process this action in time. Please try again.");
  assert.deepEqual(requests[1], { type: "callbackCancel", requestId: requests[0].requestId });
  callbackChannel.resolve({
    requestId: requests[0].requestId,
    payload: { toast: { type: "success", content: "Received." } },
  });
});
