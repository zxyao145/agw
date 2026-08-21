// 单一来源：Mobile 独立于 pnpm workspace，无法通过 node_modules 解析 @agw/execution-core，
// 因此这里统一计算其源码绝对路径，供 metro.config.js 与 jest.config.js 引用，
// 避免三处相对路径手工漂移。
// tsconfig.json 的 "paths" 为 JSON 无法 require，需与此处保持同步（见 README.md「跨 workspace 共享」）。
const path = require("path");

const executionCoreSource = path.resolve(__dirname, "../../packages/execution-core/src");
const executionCoreEntry = path.join(executionCoreSource, "index.ts");

module.exports = { executionCoreSource, executionCoreEntry };
