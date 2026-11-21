export function createWebhookPayload(
  message: Record<string, unknown>,
  webhookSecret: string | null | undefined,
  currentTime?: number,
): Record<string, unknown>;
