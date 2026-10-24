// 确认窗口的页面入口：读取请求、绘制、按内容高度显示窗口，回答一次。样式在 confirmation.css 中单独构建。
// The confirmation window page entry: reads the request, renders it, shows the window at the content height and
// answers once. The styles build separately from confirmation.css.
import type { AgwConfirmationBridge, ConfirmationAnswer } from "../shared/contracts";
import { renderConfirmation } from "./confirmation-view";

declare global {
  interface Window {
    /** 确认窗口的 preload 提供。Provided by the confirmation window preload. */
    agwConfirmation: AgwConfirmationBridge;
  }
}

async function start(bridge: AgwConfirmationBridge): Promise<void> {
  const { requestId, view } = await bridge.request();
  let answered = false;
  const answer = (value: ConfirmationAnswer) => {
    if (answered) return;
    answered = true;
    bridge.answer(requestId, value);
  };
  const { cancel, focus } = renderConfirmation(
    document.getElementById("confirmation")!,
    view,
    answer,
  );
  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") return;
    event.preventDefault();
    answer(cancel);
  });
  focus();
  bridge.ready(requestId, document.documentElement.scrollHeight);
}

void start(window.agwConfirmation);
