// 构建检查：推送给 extension 的主题变量（THEME_TOKEN_NAMES）在 renderer 构建的 CSS 中都有定义，并且可以从中提取默认变量样式表。
// Build check: every theme variable pushed to extensions (THEME_TOKEN_NAMES) is defined in the renderer's built CSS,
// and the default variable stylesheet can be extracted from it.
import { pathToFileURL } from "node:url";

import {
  checkThemeTokenNames,
  extractThemeDefaults,
  loadThemeTokenNames,
  readBuiltHostCss,
} from "./host-theme-css.mjs";

export async function checkThemeTokens() {
  const stylesheets = await readBuiltHostCss();
  const names = await loadThemeTokenNames();
  checkThemeTokenNames(stylesheets, names, "The built renderer CSS");
  extractThemeDefaults(stylesheets, names);
  return stylesheets.map(({ file }) => file);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const files = await checkThemeTokens();
  console.log(`Theme variables are defined in ${files.length} built CSS file(s).`);
}
