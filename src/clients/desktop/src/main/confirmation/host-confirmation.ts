import type {
  ConfirmationAnswer,
  ConfirmationOrigin,
  ConfirmationView,
  HostConfirmationRequest,
} from "../../shared/contracts";

/** 只保留来源的已知字段；未知的来源抛出错误。Keeps the known fields of an origin; an unknown origin throws. */
function originOf(origin: ConfirmationOrigin): ConfirmationOrigin {
  switch (origin.kind) {
    case "host":
      return { kind: "host" };
    case "extension":
      return {
        kind: "extension",
        extensionId: String(origin.extensionId),
        extensionName: String(origin.extensionName),
      };
    default:
      throw new Error("Unknown confirmation origin.");
  }
}

/**
 * 宿主页面的确认请求转换为确认窗口的内容，只保留已知字段；`clientId` 单独返回。
 * Converts a host page confirmation request into confirmation window content, keeping only known fields; `clientId`
 * is returned separately.
 */
export function hostConfirmationView(request: HostConfirmationRequest): {
  view: ConfirmationView;
  clientId: string | null;
} {
  switch (request.kind) {
    case "discard-unsaved":
      return {
        view: {
          kind: "discard-unsaved",
          items: request.items.map((item) => ({
            fileName: String(item.fileName),
            viewerTitle: String(item.viewerTitle),
            origin: originOf(item.origin),
          })),
        },
        clientId: null,
      };
    case "open-link":
      return {
        view: {
          kind: "open-link",
          extensionId: String(request.extensionId),
          extensionName: String(request.extensionName),
          url: String(request.url),
        },
        clientId: null,
      };
    case "message":
      return {
        view: {
          kind: "message",
          origin: originOf(request.origin),
          type: request.type,
          message: String(request.message),
          detail: request.detail === null ? null : String(request.detail),
          buttons: request.buttons.map(String),
          defaultId: request.defaultId,
          cancelId: request.cancelId,
        },
        clientId: String(request.clientId),
      };
    default:
      throw new Error("Unknown confirmation request.");
  }
}

/** 取消时的回答：`message` 为 `cancelId`，其他为 false。The answer for a cancel: `cancelId` for `message`, false otherwise. */
export function canceledAnswer(view: ConfirmationView): ConfirmationAnswer {
  return view.kind === "message" ? view.cancelId : false;
}
