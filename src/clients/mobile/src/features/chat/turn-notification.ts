import type { TurnFinishedStatus } from "@agw/execution-core";

export type TurnNotifyStatus = Exclude<TurnFinishedStatus, "interrupted">;

/** 仅接受白名单内的终态；interrupted 多为用户主动停止，不弹通知。 */
export function toTurnNotifyStatus(value: TurnFinishedStatus): TurnNotifyStatus | null {
  return value === "completed" || value === "failed" ? value : null;
}

export function getTurnNotificationContent(status: TurnNotifyStatus): {
  title: string;
  body: string;
} {
  return status === "completed"
    ? { title: "Turn completed", body: "A running task in Agw has finished." }
    : { title: "Turn failed", body: "A running task in Agw has failed." };
}
