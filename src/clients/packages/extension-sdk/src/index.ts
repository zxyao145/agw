// extension SDK、公开 API 类型、RPC 协议与权限常量。宿主页面与 extension 包都从这里引用。
// Desktop 提供给 extension 页面的 `/__agw/sdk.js` 由 `sdk/index.ts` 单独构建，只包含 connect、defineBackground、defineView。
// The extension SDK, public API types, RPC protocol and permission constants, shared by the Desktop host and extension
// packages. The `/__agw/sdk.js` served to extension pages is built from `sdk/index.ts` alone and exports only
// connect, defineBackground and defineView.
export type * from "./api";
export * from "./permissions";
export * from "./rpc";
// `api.ts` 的 ExtensionError 接口由这个类实现；入口导出类。The class implements the ExtensionError interface of `api.ts`; the entry exports the class.
export { ExtensionError } from "./rpc";
export { connect, defineBackground, defineView } from "./sdk/index";
export { createExtensionApi, type EventRouter } from "./sdk/api";
export { SDK_MANIFEST_VERSION } from "./sdk/connection";
