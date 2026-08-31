import type { TurnNotificationStatus } from "../shared/contracts";

/** 仅接受白名单内的终态；其他负载一律丢弃，避免渲染层注入任意通知文案。 */
export function normalizeTurnNotificationStatus(value: unknown): TurnNotificationStatus | null {
  if (typeof value !== "object" || value === null) return null;
  const status = (value as { status?: unknown }).status;
  return status === "completed" || status === "failed" ? status : null;
}

export function getTurnNotificationText(status: TurnNotificationStatus): {
  title: string;
  body: string;
} {
  return status === "completed"
    ? { title: "Turn completed", body: "A running task in Agw Desktop has finished." }
    : { title: "Turn failed", body: "A running task in Agw Desktop has failed." };
}
