// Agw Desktop extension SDK。extension 用 `import { defineView } from "/__agw/sdk.js"` 引用。
// The Agw Desktop extension SDK, imported with `import { defineView } from "/__agw/sdk.js"`.
import type { BackgroundDefinition, ExtensionApi, ViewDefinition } from "../api";
import { connect as connectToHost, registerBackground, registerView } from "./connection";

export type * from "../api";

/** 建立连接并返回 API。Connects and returns the API. */
export function connect(): Promise<ExtensionApi> {
  return connectToHost();
}

/** 定义 background 钩子与命令。Defines background hooks and commands. */
export function defineBackground(definition: BackgroundDefinition): void {
  registerBackground(definition);
  void connectToHost();
}

/** 定义视图钩子。Defines view hooks. */
export function defineView(definition: ViewDefinition): void {
  registerView(definition);
  void connectToHost();
}
