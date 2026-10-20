// monaco-editor 0.57.0 的 language/json/monaco.contribution.js 没有类型声明。它只是从 languages/features/json/register.js
// 重新导出 getWorker 与 jsonDefaults，这里沿用那个文件的声明。
// monaco-editor 0.57.0 ships no declaration for language/json/monaco.contribution.js. It only re-exports getWorker and
// jsonDefaults from languages/features/json/register.js, so this reuses that file's declaration.
declare module "monaco-editor/language/json/monaco.contribution" {
  export { getWorker, jsonDefaults } from "monaco-editor/languages/features/json/register";
}
