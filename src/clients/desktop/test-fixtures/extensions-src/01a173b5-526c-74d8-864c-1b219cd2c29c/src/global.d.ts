import type { ExtensionApi } from "/__agw/sdk.js";

declare global {
  interface Window {
    /** 测试通过 executeJavaScript 使用的 API。The API the test uses through executeJavaScript. */
    api: ExtensionApi;
  }
}
