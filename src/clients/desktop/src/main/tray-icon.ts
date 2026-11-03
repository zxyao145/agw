/**
 * 托盘图标在 `assets/` 下的文件名，由 `scripts/build-tray-icons.mjs` 从 `tray-icon.svg` 生成：nativeImage 只解码 PNG、
 * JPEG 与 Windows 上的 ICO，SVG 只会得到空图像。macOS 使用模板图（`@2x` 同名文件自动加载）；Windows 的托盘按系统小图标
 * 尺寸从 ICO 中取图；Linux 面板缩小 PNG。
 * The tray icon's file name under `assets/`, generated from `tray-icon.svg` by `scripts/build-tray-icons.mjs`:
 * nativeImage decodes only PNG, JPEG and, on Windows, ICO, so an SVG yields an empty image. macOS uses a template image
 * (the `@2x` sibling loads automatically), the Windows tray picks the system small-icon size from the ICO, and Linux
 * panels scale the PNG down.
 */
export function trayIconFileName(platform: NodeJS.Platform): string {
  if (platform === "darwin") return "tray-icon-Template.png";
  if (platform === "win32") return "tray-icon.ico";
  return "tray-icon.png";
}
