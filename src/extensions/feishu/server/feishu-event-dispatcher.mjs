import { randomUUID } from "node:crypto";
import * as Lark from "@larksuiteoapi/node-sdk";

const CALLBACK_RESPONSE_TIMEOUT_MS = 2500;
const CALLBACK_TIMEOUT_RESPONSE = {
  toast: { type: "error", content: "Could not process this action in time. Please try again." },
};

export function createCallbackChannel(send) {
  const pending = new Map();
  return {
    request(request) {
      return new Promise((resolve) => {
        if (pending.has(request.requestId))
          throw new Error("The Feishu card action request id is duplicated.");
        const timeout = setTimeout(() => {
          const waiting = pending.get(request.requestId);
          if (!waiting) return;
          send({ type: "callbackCancel", requestId: request.requestId });
          pending.delete(request.requestId);
          waiting.resolve(CALLBACK_TIMEOUT_RESPONSE);
        }, CALLBACK_RESPONSE_TIMEOUT_MS);
        pending.set(request.requestId, { resolve, timeout });
        try {
          send({ type: "callback", ...request });
        } catch (error) {
          clearTimeout(timeout);
          pending.delete(request.requestId);
          throw error;
        }
      });
    },
    resolve({ requestId, payload }) {
      const waiting = pending.get(requestId);
      if (!waiting) return;
      clearTimeout(waiting.timeout);
      pending.delete(requestId);
      waiting.resolve(payload);
    },
  };
}

export function createEventDispatcher({
  appRecordId,
  dispatcherOptions,
  sendEvent,
  requestCardAction,
}) {
  return new Lark.EventDispatcher(dispatcherOptions).register({
    "im.message.receive_v1": async (payload) => {
      sendEvent({ appRecordId, eventKey: "im.message.receive_v1", payload });
    },
    "card.action.trigger": async (payload) => {
      const response = await requestCardAction({
        appRecordId,
        requestId: randomUUID(),
        eventKey: "card.action.trigger",
        payload,
      });
      return {
        ...(response.toast ? { toast: response.toast } : {}),
        ...(response.card ? { card: response.card } : {}),
      };
    },
  });
}
