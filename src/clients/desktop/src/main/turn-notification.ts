import type { TurnNotificationRequest, TurnNotificationStatus } from "../shared/contracts";

/** 通知标题（会话名）的最大显示长度（含省略号），按 Unicode 码点计。 */
export const MAX_TURN_NOTIFICATION_TITLE_LENGTH = 64;

/** 仅接受白名单内的终态；其他负载一律丢弃，避免渲染层注入任意通知文案。 */
export function normalizeTurnNotificationStatus(value: unknown): TurnNotificationStatus | null {
  if (typeof value !== "object" || value === null) return null;
  const status = (value as { status?: unknown }).status;
  return status === "completed" || status === "failed" ? status : null;
}

/** 会话标题来自渲染层，仅作纯文本展示：去控制字符、折叠空白、按码点截断。 */
export function normalizeTurnNotificationRequest(value: unknown): TurnNotificationRequest | null {
  const status = normalizeTurnNotificationStatus(value);
  if (!status) return null;
  return { status, title: sanitizeNotificationTitle((value as { title?: unknown }).title) };
}

function sanitizeNotificationTitle(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const cleaned = value.replace(/\p{C}/gu, " ").replace(/\s+/g, " ").trim();
  if (!cleaned) return undefined;
  const codePoints = Array.from(cleaned);
  if (codePoints.length <= MAX_TURN_NOTIFICATION_TITLE_LENGTH) return cleaned;
  return `${codePoints.slice(0, MAX_TURN_NOTIFICATION_TITLE_LENGTH - 1).join("")}…`;
}

export function getTurnNotificationText(
  status: TurnNotificationStatus,
  title?: string,
): {
  title: string;
  body: string;
} {
  const body =
    status === "completed"
      ? "A running task in Agw Desktop has finished."
      : "A running task in Agw Desktop has failed.";
  return { title: title ?? (status === "completed" ? "Turn completed" : "Turn failed"), body };
}
