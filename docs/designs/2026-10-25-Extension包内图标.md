# Extension 包内图标的 SVG 与图片模式

## 名词解释

- **包内图标**：manifest 的 `icon` 字段（顶层 `icon` 与 `contributes` 中各项的 `icon`）引用的 extension 包内文件。其余取值为 lucide-react 导出名。
- **SVG 模式**：`.svg` 包内图标的显示方式。宿主取图标中不透明的部分作为形状，用图标所在位置的文字颜色（`currentColor`）填充。
- **图片模式**：`.png`、`.jpg`、`.jpeg`、`.webp` 包内图标的显示方式。宿主按原图显示。
- 其余名词沿用 `2026-10-16-Desktop-Extension.md`。

## 背景

- `2026-10-16-Desktop-Extension.md` 规定包内图标为 `.svg` 或 `.png`，主进程扫描时把它转换为 data URL，宿主用 `<img>` 显示。`<img>` 中的 SVG 是独立的文档，`currentColor` 取不到宿主页面的文字颜色，按黑色绘制。Lucide 等图标库的 SVG 使用 `stroke="currentColor"`，在深色 Mode 下显示为深色卡片上的黑色图标。
- Desktop 的 Mode 由 next-themes 写在 `<html>` 的 class 上，Electron 的 `nativeTheme` 不随它变化。SVG 内部的 `prefers-color-scheme` 跟随系统设置，Mode 与系统设置不同时会选错颜色。
- 包内图片只支持 PNG，JPEG 与 WebP 图片不能作为图标。
- 内置的 Excalidraw、Image Viewer、Text Editor 没有声明顶层 `icon`，Extensions 页面中显示默认的 Puzzle 图标。

## 设计目标

### 功能目标

1. 包内 SVG 图标按 SVG 模式显示，颜色跟随主题与 Mode。
2. 包内图片图标支持 PNG、JPEG（`.jpg`、`.jpeg`）与 WebP，按图片模式显示。
3. Excalidraw、Image Viewer、Text Editor 使用 Lucide 的 SVG 图标。

### 技术目标

- **安全**：SVG 仍作为图片资源加载（CSS `mask-image` 引用 data URL），不插入宿主 DOM。SVG 中的脚本、事件属性与样式不在宿主页面中执行，也不影响宿主页面的样式。
- **兼容**：`manifestVersion` 仍为 1，新增的图片扩展名是增量，已经使用 `.svg`、`.png` 图标的 extension 不需要修改。多色 SVG 图标按 SVG 模式显示为单色，需要保留颜色的图标使用 PNG、JPEG 或 WebP。
- **体积**：单个图标文件仍不超过 256 KB（`ICON_TOO_LARGE`）。

## 详细设计

### 1. manifest 与主进程

- `desktop/src/shared/extension-manifest.ts` 中包内图标的扩展名为 `svg`、`png`、`jpg`、`jpeg`、`webp`，与其他包路径的扩展名一样区分大小写。`build-extension-sdk.mjs` 按同一个 zod schema 生成 `manifest.schema.json`，生成结果随之变化。
- `desktop/src/main/extensions/extension-catalog.ts` 按扩展名决定 data URL 的 MIME：`.svg` 为 `image/svg+xml`，`.png` 为 `image/png`，`.jpg` 与 `.jpeg` 为 `image/jpeg`，`.webp` 为 `image/webp`。
- `@agw/extension-sdk` 中 `icon` 字段的注释列出这些扩展名与两种显示模式。

### 2. 宿主显示

- `WorkspaceIcon` 增加 `{ kind: "svg"; src: string }`。`resolveIcon` 对 `.svg` 包内图标返回 `svg`，对其他包内图标返回 `image`，对 lucide-react 导出名返回 `lucide`，未声明时为 `Puzzle`。
- `WorkspaceIconView`：
  - `svg`：渲染 `<span aria-hidden="true">`，`background-color: currentColor`，`mask-image: url("<data URL>")`，`mask-size: contain`，`mask-repeat: no-repeat`，`mask-position: center`。默认尺寸与 lucide 图标相同（`size-4`），调用方的 className 可以覆盖。
  - `image`：渲染 `<img>`，与现有行为相同。
- 显示包内图标的位置（Extensions 页面卡片、Ribbon、视图标题条、文件查看器的 More 菜单、对话中的 extension 操作）都经过 `WorkspaceIconView`，同时使用新的显示方式。

### 3. 内置 extension 图标

- 图标取自 `lucide-static@1.56.0`（与 catalog 中 `lucide-react` 的版本相同），使用原文件，保留其中的 ISC 许可注释：
  - Excalidraw：`pen-tool.svg`，与它的文件查看器图标 `PenTool` 相同。
  - Image Viewer：`image.svg`，与它的文件查看器图标 `Image` 相同。
  - Text Editor：`file-text.svg`。
- 文件放在各包的 `public/icon.svg`，Vite 构建时复制为 `dist/icon.svg`；manifest 顶层声明 `"icon": "icon.svg"`。
- `build-bundled-extensions.mjs` 按 Desktop 读取 extension 目录的方式校验 `dist/` 中的包路径与图标，不需要修改。

## 测试计划

### 单元测试

- `extension-manifest.test.ts`：`.jpg`、`.jpeg`、`.webp` 图标合法；`.gif` 图标为 `INVALID_FIELD`。
- `extension-catalog.test.ts`：PNG、JPEG、WebP 图标的 data URL 带有对应的 MIME，解码后与图标文件相同。
- `extension-contributions.test.ts`：`resolveIcon` 对 `.svg` 返回 `svg`，对 `.png`、`.jpg`、`.webp` 返回 `image`，对 lucide-react 导出名返回 `lucide`，未声明时返回 `Puzzle`。
- `workspace-icon.test.ts`：`svg` 渲染为带有 `mask-image` 与 `bg-current` 的 `<span>`，没有 `<img>`；`image` 渲染为 `<img>`，`src` 为 data URL。
- `workspace-registry.test.ts`：Ribbon 项的 `.svg` 图标为 `{ kind: "svg" }`。

### 集成测试

- `extensions.integration.ts`：Extensions 页面中 Text Editor、Excalidraw、Image Viewer 的卡片没有 `<img>`，图标计算样式中的 `mask-image` 引用 `data:image/svg+xml` 的 data URL，`background-color` 等于卡片标题的文字颜色。`bundledDefaults()` 在首次启动时检查，`darkModeAndExtensionViews()` 在 Soft 主题的深色 Mode 下再检查一次。
