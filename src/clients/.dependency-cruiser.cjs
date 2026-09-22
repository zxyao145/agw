// Import boundaries between the client workspaces. Paths are relative to src/clients and
// workspace packages resolve through node_modules symlinks to their real `packages/<name>/src` path.
// 客户端各工作区之间的导入边界。路径相对于 src/clients，工作区包经 node_modules 符号链接解析到
// 真实的 `packages/<name>/src` 路径。

const NATIVE_RENDERER = String.raw`(^|/)node_modules/(react-native|expo|expo-[^/]*)/`;
const DOM_RENDERER = String.raw`(^|/)node_modules/(next|react-dom)/`;
const MOBILE_SAFE_PACKAGES = "api|chat-native|execution-core|projects-core";

/** @type {import('dependency-cruiser').IConfiguration} */
module.exports = {
  forbidden: [
    {
      name: "packages-no-application-imports",
      comment: "Workspace packages never import Web, Desktop, or Mobile application code.",
      severity: "error",
      from: { path: "^packages/" },
      to: { path: ["^(web|desktop|mobile)/", "^@/"] },
    },
    {
      name: "no-cross-workspace-relative-import",
      comment: "A relative import must stay inside the workspace that owns the source file.",
      severity: "error",
      from: { path: "^((?:packages/[^/]+)|web|desktop|mobile)/" },
      to: { dependencyTypes: ["local"], pathNot: "^$1/" },
    },
    {
      name: "web-desktop-isolation",
      comment: "Web and Desktop never import each other.",
      severity: "error",
      from: { path: "^(web|desktop)/" },
      to: { path: "^(web|desktop)/", pathNot: "^$1/" },
    },
    {
      name: "mobile-react-native-safe-packages-only",
      comment: "Mobile consumes @agw/chat-native plus React Native-safe packages only.",
      severity: "error",
      from: { path: "^mobile/" },
      to: { path: "^(web|desktop|packages)/", pathNot: `^packages/(${MOBILE_SAFE_PACKAGES})/` },
    },
    {
      name: "chat-core-runtime-platform-neutral",
      comment: "chat-core and chat-runtime stay free of DOM, Native, and shared UI dependencies.",
      severity: "error",
      from: { path: "^packages/(chat-core|chat-runtime)/" },
      to: { path: ["^packages/components/", DOM_RENDERER, NATIVE_RENDERER] },
    },
    {
      name: "chat-dom-renderer-only",
      comment: "@agw/chat is the DOM renderer and never imports React Native or Expo.",
      severity: "error",
      from: { path: "^packages/chat/" },
      to: { path: NATIVE_RENDERER },
    },
    {
      name: "chat-native-no-dom-renderer",
      comment: "@agw/chat-native never imports the DOM renderer or shared DOM UI.",
      severity: "error",
      from: { path: "^packages/chat-native/" },
      to: { path: ["^packages/(components|chat)/", DOM_RENDERER] },
    },
    {
      name: "react-query-through-components",
      comment: "DOM code reaches @tanstack/react-query through @agw/components/query.",
      severity: "error",
      from: { path: "^(web|desktop|packages)/", pathNot: "^packages/(components|chat-native)/" },
      to: { path: String.raw`(^|/)node_modules/@tanstack/react-query/` },
    },
    {
      name: "accordion-through-components",
      comment: "Accordion primitives are wrapped once in @agw/components.",
      severity: "error",
      from: { pathNot: "^packages/components/" },
      to: { path: String.raw`(^|/)node_modules/@radix-ui/react-accordion/` },
    },
    {
      name: "no-self-barrel-import",
      comment: "A package never imports its own public barrel.",
      severity: "error",
      from: { path: "^packages/([^/]+)/src/" },
      to: { path: String.raw`^packages/$1/src/index\.ts$`, dependencyTypesNot: ["local"] },
    },
    {
      name: "no-unresolvable-workspace-import",
      comment: "Every @agw/* import must resolve to an exported entry point.",
      severity: "error",
      from: {},
      to: { path: "^@agw/", couldNotResolve: true },
    },
  ],
  options: {
    doNotFollow: { path: ["node_modules"] },
    exclude: { path: String.raw`(^|/)(\.next|out|dist|android|ios)/` },
    tsPreCompilationDeps: false,
    parser: "swc",
    enhancedResolveOptions: {
      exportsFields: ["exports"],
      conditionNames: ["import", "require", "node", "default", "types"],
      mainFields: ["module", "main", "types", "typings"],
      extensions: [".ts", ".tsx", ".d.ts", ".js", ".jsx", ".mjs", ".cjs", ".json"],
    },
    reporterOptions: {
      text: { highlightFocused: true },
    },
  },
};
