import { createHmac } from "node:crypto";

export function createWebhookPayload(message, webhookSecret, currentTime = Date.now()) {
  const payload = { ...message };
  if (!webhookSecret) return payload;
  const timestamp = Math.floor(currentTime / 1000).toString();
  const sign = createHmac("sha256", `${timestamp}\n${webhookSecret}`).update("").digest("base64");
  return { ...payload, timestamp, sign };
}
