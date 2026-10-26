import type { ExtensionApi, ThemeInfo } from "@agw/extension-sdk";

/**
 * 页面存续期间，主题提供工作区背景（`--agw-workspace-backdrop` 不为 `none`）时在 `html` 上设置
 * `data-workspace-backdrop`，Mixed 与 Preview 的样式据此显示背景图形与卡片；主题变化时更新。
 * For the page's lifetime, sets `data-workspace-backdrop` on `html` while the theme provides a workspace backdrop
 * (`--agw-workspace-backdrop` other than `none`); Mixed and Preview styles use it to show the backdrop and the card. It
 * updates on theme changes.
 */
export function applyWorkspaceBackdrop(api: ExtensionApi): void {
  const update = (theme: ThemeInfo) => {
    const backdrop = theme.tokens["--agw-workspace-backdrop"];
    document.documentElement.toggleAttribute(
      "data-workspace-backdrop",
      backdrop !== undefined && backdrop !== "none",
    );
  };
  update(api.theme.get());
  api.theme.onDidChange(update);
}
