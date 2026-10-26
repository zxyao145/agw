import { confirmationOriginName, HOST_ORIGIN_NAME } from "../shared/confirmation-origin";
import type {
  ConfirmationAnswer,
  ConfirmationOrigin,
  ConfirmationView,
  PermissionApprovalView,
} from "../shared/contracts";

type ButtonSpec = {
  label: string;
  answer: ConfirmationAnswer;
  tone: "primary" | "danger" | "plain";
};

const MESSAGE_TYPES = { info: "Information", warning: "Warning", error: "Error" } as const;

function element<K extends keyof HTMLElementTagNameMap>(
  document: Document,
  tag: K,
  className?: string,
  text?: string,
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

/**
 * 未保存文件后面的来源：宿主的查看器（Source）没有括注，extension 的查看器带名称与 id。
 * The origin after an unsaved file: the host's viewer (Source) has none, an extension's viewer shows its name and id.
 */
function originSuffix(origin: ConfirmationOrigin): string {
  return origin.kind === "host" ? "" : ` (${origin.extensionName}, ${origin.extensionId})`;
}

/** 权限批准的正文：权限说明、网络访问与提示全部由应用生成。The approval body; descriptions, network access and notes all come from the app. */
function permissionBody(document: Document, view: PermissionApprovalView): HTMLElement[] {
  const nodes: HTMLElement[] = [];
  if (view.downgrade) {
    nodes.push(
      element(
        document,
        "p",
        "notice warning",
        "You are installing an older version than the one installed.",
      ),
    );
  }
  if (view.permissions.length === 0 && view.hostPermissions.length === 0) {
    nodes.push(element(document, "p", "muted", "This extension requests no permissions."));
    return nodes;
  }
  nodes.push(
    element(
      document,
      "p",
      undefined,
      view.kind === "install"
        ? "Installing gives the extension these permissions:"
        : "The extension will receive these permissions:",
    ),
  );
  if (view.permissions.length > 0) {
    const list = element(document, "ul", "permissions");
    for (const permission of view.permissions) {
      const item = element(document, "li", permission.highRisk ? "high-risk" : undefined);
      item.dataset.permission = permission.name;
      item.append(element(document, "code", undefined, permission.name));
      item.append(document.createTextNode(` — ${permission.description}`));
      list.append(item);
    }
    nodes.push(list);
  }
  if (view.hostPermissions.length > 0) {
    nodes.push(element(document, "p", "section-title", "Network access"));
    const list = element(document, "ul", "hosts");
    for (const host of view.hostPermissions) {
      const item = element(document, "li");
      item.append(element(document, "code", undefined, host.pattern));
      if (host.unencrypted)
        item.append(element(document, "span", "warning", " Unencrypted connection"));
      list.append(item);
    }
    nodes.push(list);
  }
  if (view.sendsData) {
    nodes.push(
      element(
        document,
        "p",
        "notice warning",
        "This extension can send the data it reads to the hosts listed above.",
      ),
    );
  }
  if (view.runsAgents) {
    nodes.push(
      element(
        document,
        "p",
        "muted",
        "This extension can run your Agents and answer tool approvals on your behalf.",
      ),
    );
  }
  return nodes;
}

/**
 * 在 `container` 中绘制确认内容：请求来源、extension 名称与 id、待批准的权限或目标地址。来自 extension 的文字按文本渲染；
 * 权限说明、动作含义与批准按钮由应用控制。返回取消时的回答与设置初始焦点的函数。
 * Renders the confirmation into `container`: the request source, the extension name and id, and the permissions or
 * target address awaiting approval. Text from extensions renders as text; permission descriptions, action meanings
 * and approval buttons are controlled by the app. Returns the cancel answer and a function that sets the initial
 * focus.
 */
export function renderConfirmation(
  container: HTMLElement,
  view: ConfirmationView,
  onAnswer: (answer: ConfirmationAnswer) => void,
): { cancel: ConfirmationAnswer; focus(): void } {
  const document = container.ownerDocument;
  let title: string;
  let source: string;
  let body: HTMLElement[];
  let buttons: ButtonSpec[];
  let cancel: ConfirmationAnswer = false;
  let focused = 0;
  switch (view.kind) {
    case "discard-unsaved": {
      title = "Discard unsaved changes?";
      source = HOST_ORIGIN_NAME;
      const list = element(document, "ul", "files");
      for (const item of view.items) {
        const entry = element(document, "li");
        entry.append(element(document, "strong", undefined, item.fileName));
        entry.append(
          element(document, "span", "muted", ` · ${item.viewerTitle}${originSuffix(item.origin)}`),
        );
        list.append(entry);
      }
      body = [element(document, "p", undefined, "These changes have not been saved:"), list];
      buttons = [
        { label: "Cancel", answer: false, tone: "plain" },
        { label: "Discard", answer: true, tone: "danger" },
      ];
      break;
    }
    case "open-link":
      title = "Open link?";
      source = `Requested by ${view.extensionName} (${view.extensionId})`;
      body = [
        element(
          document,
          "p",
          undefined,
          "This extension wants to open this link in your browser:",
        ),
        element(document, "p", "url", view.url),
      ];
      buttons = [
        { label: "Cancel", answer: false, tone: "plain" },
        { label: "Open", answer: true, tone: "primary" },
      ];
      break;
    case "message":
      title = confirmationOriginName(view.origin);
      source =
        view.origin.kind === "host"
          ? HOST_ORIGIN_NAME
          : `Message from ${view.origin.extensionName} (${view.origin.extensionId})`;
      body = [];
      if (view.type !== null) {
        body.push(element(document, "p", `message-type ${view.type}`, MESSAGE_TYPES[view.type]));
      }
      body.push(element(document, "p", "message", view.message));
      if (view.detail !== null) body.push(element(document, "p", "detail muted", view.detail));
      buttons = view.buttons.map((label, index) => ({
        label,
        answer: index,
        tone: index === view.defaultId ? "primary" : "plain",
      }));
      cancel = view.cancelId;
      focused = view.defaultId ?? 0;
      break;
    case "install":
    case "enable":
      title = `${view.kind === "install" ? "Install" : "Enable"} ${view.extensionName}?`;
      source = `${view.extensionName} (${view.extensionId}) · Version ${view.version}${
        view.installedVersion === null ? "" : ` · Replaces version ${view.installedVersion}`
      }`;
      body = permissionBody(document, view);
      buttons = [
        { label: "Cancel", answer: false, tone: "plain" },
        {
          label:
            view.kind === "enable"
              ? "Enable"
              : view.installedVersion === null
                ? "Install"
                : "Replace",
          answer: true,
          tone: "primary",
        },
      ];
      break;
  }

  const header = element(document, "header");
  header.append(
    element(document, "h1", undefined, title),
    element(document, "p", "source", source),
  );
  const content = element(document, "section", "body");
  content.append(...body);
  const footer = element(document, "footer");
  const nodes = buttons.map((spec, index) => {
    const button = element(document, "button", spec.tone, spec.label);
    button.type = "button";
    button.dataset.answer = String(index);
    button.addEventListener("click", () => {
      for (const node of footer.querySelectorAll("button")) node.disabled = true;
      onAnswer(spec.answer);
    });
    return button;
  });
  footer.append(...nodes);
  container.replaceChildren(header, content, footer);
  container.dataset.kind = view.kind;
  return { cancel, focus: () => nodes[focused]?.focus() };
}
