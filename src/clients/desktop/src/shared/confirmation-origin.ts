import type { ConfirmationOrigin } from "./contracts";

/** 宿主自身作为来源时显示的名称。The name shown when the host itself is the origin. */
export const HOST_ORIGIN_NAME = "Agw Desktop";

/** 来源的名称：宿主为 "Agw Desktop"，extension 为它的名称。The origin's name: "Agw Desktop" or the extension's name. */
export function confirmationOriginName(origin: ConfirmationOrigin): string {
  return origin.kind === "host" ? HOST_ORIGIN_NAME : origin.extensionName;
}
