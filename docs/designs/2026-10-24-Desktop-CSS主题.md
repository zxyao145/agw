# Desktop CSS 主题

## 名词解释

- **CSS 主题**：通过 `contributes.themes` 声明 CSS 文件的主题，可以定义 CSS 自定义变量、组件样式和布局。
- **JSON 主题**：通过 JSON 文件提供 `common`、`light`、`dark` 变量值的主题。
- **主题样式文本**：读取 CSS 入口、展开包内 `@import` 并内嵌资源后得到的 `cssText`，由宿主和接入主题的 extension 页面使用。
- **默认变量样式表**：构建时从宿主 CSS 提取的根元素变量规则，供 SDK 建立与宿主相同的默认变量来源。
- **`styleId`**：主题样式文本 UTF-8 内容的 SHA-256，用于内容识别、缓存和增量传输。
- 其余名词（主题、Mode、Default、主题变量、`theme.apply()`、Main Workspace）沿用 `2026-10-23-Desktop主题.md`、`2026-10-24-工作区几何背景与主题名称.md`。

## 背景

- 当前主题通过 JSON 提供 CSS 变量值，宿主根据变量设置外观，extension 通过 `theme.apply()` 使用公共变量。
- 用户需要直接通过 CSS 文件编写主题，设置自定义变量、组件样式和布局。
- 宿主和 extension 使用独立页面。主题 CSS 需要分别应用到宿主以及主动接入主题的 extension 页面。
- 现有 JSON 主题继续可用，主题中的图片、字体和样式依赖由主题 extension 包提供。

## 设计目标

### 功能目标

1. `contributes.themes[].path` 同时接受 JSON 和 CSS 文件，现有 JSON 主题及内置主题继续使用。
2. CSS 主题支持任意自定义变量、组件样式和 DOM 布局，使用浏览器的 CSS 级联规则；原生视图的同步范围在详细设计中定义。
3. 支持包内 `@import`、图片、字体和 data URL，主题可以离线使用。
4. 宿主应用主题 CSS；调用 `theme.apply()` 的 extension 页面应用同一份主题样式。
5. 切换主题、Mode、停用或删除主题 extension 时更新样式，保持页面、文档及未保存内容。
6. 主题引起布局变化后，extension 的 `WebContentsView` 跟随占位区域更新位置、尺寸和可见性。
7. 文件读取、CSS 结构解析或资源检查失败时提供明确的校验错误，安装校验与内置 extension 构建使用相同的主题加载逻辑。
8. 用户确认和权限批准使用独立的受控页面，主题不能修改这些页面的内容、按钮或显示状态。

### 技术目标

- **CSS 解析**：使用成熟的 CSS 解析库处理语法和资源引用。
- **包内资源**：每个引用文件都经过实际路径检查，资源范围限定在主题 extension 根目录内。
- **失败直接报告**：依赖缺失、路径超出包目录、外部资源引用或解析失败时，返回 `ExtensionValidationError`。
- **样式生命周期**：每个 document 复用自己创建的 `CSSStyleSheet`，只有 `styleId` 改变时替换主题文本，Mode 变化通过现有的 `dark` class 生效。
- **资源预算**：限制主题输入、展开次数和内嵌后的体积，缓存完整依赖图的编译结果，按连接传输发生变化的样式文本。
- **兼容**：`manifestVersion` 保持为 1，`ThemeInfo.cssText` 为可选字段；JSON 主题保持现有校验和变量合并方式。

## 总体设计

Electron main 根据主题入口的扩展名选择 JSON 或 CSS 加载流程。CSS 加载流程展开包内样式依赖，将图片和字体转换为 data URL，对完整结果执行资源检查，并缓存 `cssText` 及其依赖图。extension 列表携带主题描述，Desktop renderer 根据当前主题的 `styleId` 获取样式文本。

`ThemeProvider` 根据当前主题管理宿主页面上的变量和主题样式表。实际 `dark` class 与样式更新完成后，`ExtensionHostProvider` 读取公共变量的计算值。`welcome` 提供完整主题快照，`theme.changed` 分别表达样式保留、替换和清除。SDK 将消息还原为完整的 `ThemeInfo`。

SDK 的 `theme.apply()` 在 extension 自己的 document 中应用默认变量样式表及主题。宿主持续测量已挂载的 extension 占位区域，并同步原生视图的矩形区域和可见性。用户确认和权限批准由 Electron main 管理独立的 modal `BrowserWindow`。

## 数据库设计

不涉及数据库变更。主题选择继续保存在 Desktop `settings.json` 的 `appearance.theme`，Mode 继续使用 `appearance.mode`。

## 详细设计

### 1. 主题声明与数据类型

- `contributes.themes` 保留 `id`、`title`、`path`，`path` 同时接受 `.json` 和 `.css`。
- manifest 的包内路径规则继续应用于主题入口。主题 id 与设置中的 `"{extensionId}:{themeId}"` 格式保持一致。
- 内部 `ExtensionTheme` 使用 `format: "json" | "css"` 区分数据：JSON 分支保留 `common`、`light`、`dark`；CSS 分支提供 `styleId` 和 UTF-8 `byteLength`。两个分支都包含 `id` 和 `title`，完整 CSS 保存在 main 的编译缓存中。
- JSON 分支继续按照 `common` 与当前 `light`、`dark` 合并变量，保留现有变量清单和值类型校验。
- 主题列表继续使用启用且校验通过的 extension 提供的主题。Soft、Warm Minimalism 等内置 JSON 主题继续使用现有文件。

### 2. CSS 变量、组件样式和布局

- CSS 入口使用标准 CSS，可以声明任意自定义变量、选择器、组件属性、Grid/Flex 布局、媒体查询、`@supports`、`@layer` 和 `@keyframes`。
- `:root` 中的规则用于两种 Mode；浅色专用规则使用 `:root:not(.dark)`，深色专用规则使用 `:root.dark`。宿主和 SDK 根据当前 Mode 设置根元素上的 `dark` class。
- CSS 主题中的自定义变量直接由样式表定义和使用。`ThemeInfo.tokens` 继续提供 `THEME_TOKEN_NAMES` 中公共变量的宿主计算值，扩展页面通过自己的默认变量与主题规则计算 CSS 值。
- Desktop 构建通过 CSS AST 提取宿主构建 CSS 中的全部根元素自定义属性规则，生成 `/__agw/theme-defaults.css`，包括 `--spacing`、`--text-*`、宿主构建包含的色阶和现有主题变量。提取保留选择器、来源顺序、Mode、`@media`、`@supports` 与 cascade layer 上下文，并包含变量依赖及相关 `@property` 定义。
- 默认变量提取与 `check-theme-tokens.mjs` 共用构建 CSS 的 AST 输入；名称检查和规则提取分别验证。SDK 初始化完成前加载同一 Desktop 构建生成的默认变量文本，只有调用 `theme.apply()` 时才将其应用到 document。现有 typography 组件样式继续使用。
- 宿主与 SDK 使用 constructed stylesheet 应用主题，SDK 的默认变量样式表位于主题样式表之前。每个 document 创建自己的样式表对象；`document.adoptedStyleSheets` 保留其他代码创建的样式表，仅更新主题系统拥有的部分。其来源顺序位于普通 `<style>`、`<link>` 样式表之后，包括稍后动态加入的样式，依据 [CSSOM](https://drafts.csswg.org/cssom/#dom-documentorshadowroot-adoptedstylesheets)。
- 选择器特异性、cascade layer、inline 属性和 `!important` 继续遵循 [CSS Cascade](https://www.w3.org/TR/css-cascade-5/#cascade-sort)。来源顺序只决定相同级联条件下的结果；`!important` 声明的 layer 顺序与普通声明相反。
- CSS 主题应用之前，清除主题系统之前写入的 inline 主题变量。Appearance 管理的字号属性继续由设置管理；SDK 将推送的 `--agw-font-size-base` 写入自己管理的 inline 属性，保证普通主题规则和 typography 默认值之间的字号传递。其他公共变量由默认变量和主题 CSS 计算。

### 3. 包内依赖与资源处理

#### 导入与路径

- CSS 加载流程使用 PostCSS、`postcss-import` 17.0.1 和现有 `css-tree` AST。`postcss-import` 设置 `skipDuplicates: false`，重复导入保持来源顺序，media、supports、命名及匿名 layer 保持原有条件。匿名 layer 使用该版本已有的处理能力，参考[插件文档](https://github.com/postcss/postcss-import/tree/17.0.1)及[条件处理实现](https://github.com/postcss/postcss-import/blob/17.0.1/lib/apply-conditions.js)。
- 自定义 `resolve` 对每个导入返回经过实际路径检查的绝对文件路径，`load` 只读取已检查的文件。来源 AST 建立导入依赖图，检查循环引用与资源预算。导入只接受包内 `.css`，循环导入返回校验错误；所有导入均经过解析和校验。展开结果中存在残留 `@import` 时校验失败。
- 相对路径按照发出引用的 CSS 文件所在目录解析；以 `/` 开头的包内路径从主题 extension 根目录解析。嵌套导入中的资源使用各自的来源文件目录。
- 每个依赖文件在读取之前通过 `realpath` 检查，目标必须是主题 extension 根目录内的普通文件。缓存命中时同样复核引用路径与实际路径的对应关系。

#### 资源引用校验

- 资源处理同时检查各来源文件和处理后的完整 `cssText`。`css-tree` 显式启用 `parseCustomProperty: true`；资源校验使用解析库处理 URL、函数名和 at-rule 名称中的 CSS escape，检查覆盖自定义属性、普通声明、函数参数与相关 at-rule。
- 包内图片、字体及 `image-set()`、`-webkit-image-set()` 中的字符串资源地址均转换为 data URL。处理完成后，所有具有资源加载含义的 URL 只能是已验证的 data URL 或当前 document 的片段引用，例如 `url(#filter)`。
- `image-set()` 的图片候选项必须可以静态检查：允许字面量 URL、字符串地址，以及可递归检查的图片函数。渐变候选项继续可用。候选图片地址不接受 `var()`、`env()` 或 `attr()` 等动态表达式；渐变内部的颜色和长度变量继续按照 CSS 规则使用。`image-set(var(--s) 1x)` 即使 `--s` 声明在其他位置，也返回校验错误。
- 其他能够从字符串产生资源地址的语法同样必须由解析库明确识别并检查；无法检查的资源表达式、值中的 `Raw` 节点或相关解析恢复错误使校验失败。HTTP、HTTPS、协议相对地址、其他 extension、`file:`、`blob:` 和其他包外资源引用均返回错误。
- `@namespace` 的 URI 按照 namespace 标识符处理，允许 `http://www.w3.org/2000/svg` 等名称，依据 [CSS Namespaces](https://www.w3.org/TR/css-namespaces-3/#syntax)。为保持 stylesheet 范围，包含 `@namespace` 的主题使用没有 `@import` 的单独入口；导入文件中的 namespace 或 namespace 与导入组合使用时返回明确的校验错误。

#### 文件类型与 MIME

- 图片允许 PNG、JPEG、GIF、WebP、AVIF、SVG、ICO，对应 `image/png`、`image/jpeg`、`image/gif`、`image/webp`、`image/avif`、`image/svg+xml`、`image/vnd.microsoft.icon`。
- 字体允许 WOFF、WOFF2、TTF、OTF，对应 `font/woff`、`font/woff2`、`font/ttf`、`font/otf`。`@font-face` 的 URL 使用字体类型。
- 包内文件的 MIME 通过 [`mime-types`](https://github.com/jshttp/mime-types) 的 MIME 数据库查询，并与允许的扩展名和类型核对；未知类型返回校验错误。内嵌时生成明确的 MIME 与 base64 数据。
- 已有 data URL 使用 [`data-urls`](https://github.com/jsdom/data-urls) 解析，校验 MIME、使用位置及解码后的体积。CSS、HTML、JavaScript 和未列出的 data MIME 均返回错误。
- SVG 作为图片资源使用，通过成熟 XML parser 检查其属性和内嵌 CSS；外部实体、脚本和无法检查的资源表达式返回错误。SVG 内的资源地址按照 SVG 文件所在目录解析，使用相同的包内资源、data URL 和片段规则；内嵌 CSS 使用同一套 CSS 资源检查，嵌套资源同样计入依赖图和体积预算。

### 4. 宿主与 extension 页面应用

- `ThemeProvider` 分别管理 JSON 变量和 CSS 样式表。选中 CSS 主题时获取已校验的样式文本，准备完成后更新当前主题；选中 JSON 主题时应用其合并后的变量。
- `ThemeInfo` 增加可选的 `cssText`。CSS 主题携带处理后的完整文本，JSON 和 Default 省略该字段。SDK 对外的 `theme.get()` 和 `theme.onDidChange` 始终提供完整状态。
- 宿主根元素保留 `data-agw-desktop="true"`，增加 `data-agw-document="desktop"`。SDK 为 extension 根元素设置 `data-agw-document="extension"` 与 `data-agw-extension="<extensionId>"`，extension id 来自宿主提供的 manifest。
- `theme.apply()` 同时接入变量、Mode 和 CSS 规则，调用方式保持一致。通用的 `body`、`button`、`*` 等选择器会作用于所有接入主题的 document；主题作者通过根元素标记限定宿主规则或指定 extension 的规则。
- Text Editor 和 Excalidraw 保留已有的 `theme.apply()` 调用，均接入主题 CSS。主题规则可以影响这些页面中匹配的 DOM 元素；Monaco 和 canvas 自身的绘制内容继续由对应组件控制。
- SDK 在 CSS 主题下应用默认变量样式表及主题样式表，并单独管理字号 inline 属性；在 JSON 和 Default 下保持现有变量应用方式，清除之前的 CSS 主题及默认变量样式表。
- 重复调用 `theme.apply()` 复用同一 document 的样式表。Mode 或公共变量更新时，内容相同的主题样式表继续使用。不同 document 分别创建、解析和保存自己的样式表。
- `theme.apply()` 返回的 `Disposable` 保持停止该次订阅的语义，当前外观继续保留；页面卸载时释放文档资源。宿主 provider 卸载时清理其拥有的样式表与变量。extension 是否调用 `theme.apply()` 由自身决定。

### 5. 用户确认与权限批准

- `HostConfirmationDialog` 涵盖的外部链接确认、未保存内容确认、extension 消息框，以及安装和启用 extension 的权限批准，统一通过 Electron main 的确认服务显示。
- 确认服务创建以主窗口为 parent 的 modal `BrowserWindow`，使用独立 document、固定的应用资源和样式、系统字体以及独立 preload。该窗口不接入 `theme.apply()`，不接收主题文本、主题变量或由主题决定的布局；显示位置和生命周期由 main 管理。
- 确认窗口启用 `sandbox` 和 `contextIsolation`，关闭 `nodeIntegration`，禁止页面导航和创建新窗口。确认页面通过专用本地资源入口返回 CSP 响应头，使用 `default-src 'none'`、`script-src 'self'`、`style-src 'self'`、`connect-src 'none'`、`img-src 'none'`、`frame-ancestors 'none'`，仅加载确认界面必需的资源。
- 确认界面明确显示请求来源、extension 名称和 id、待批准的权限或目标地址。来自 extension 的名称、消息和按钮文字按文本渲染；权限说明、动作含义与批准按钮由应用控制。
- main 保存一次性请求标识、所属窗口及待执行动作。批准结果只接受对应确认窗口的 IPC，并核对 sender、请求标识和仍有效的请求数据；安装或启用的请求绑定已校验的包摘要、版本与权限集合。关闭窗口、包数据变化或请求失效时按取消处理。
- 权限保存和需要批准的操作依赖确认服务返回的结果。宿主与 extension 页面中的普通点击或任意 `approved` 参数不能产生批准结果。确认期间 parent 窗口接受系统 modal 限制，主题 CSS 无法覆盖确认窗口。

### 6. 体积、缓存与状态传输

#### 资源预算

- 单个 CSS 文件最多 1 MiB，单个图片或字体资源解码后最多 4 MiB；每个主题的不同输入文件与已有 data URL 的解码数据合计最多 8 MiB。
- 每个主题最多读取 256 个不同文件，导入深度最多 32 层，导入展开累计最多 256 次。重复导入计入展开次数，避免依赖复用放大输出。
- 完整 `cssText` 按 UTF-8 计算最多 16 MiB，包括重复展开的样式、条件包装和 base64 数据。读取、导入展开与内嵌过程中累计检查预算，在构造超限结果之前终止处理；最终文本再次核对体积。
- 以上数值为实现采用的固定上限。超限返回 `ExtensionValidationError`，`code` 为 `THEME_SIZE_LIMIT_EXCEEDED`，`path` 指向主题入口声明，消息包含触发上限的文件、实际值及允许值。

#### 编译缓存

- main 缓存入口及完整依赖图，包括每个引用路径、`realpath`、文件标识、大小、纳秒级修改时间和状态变更时间，以及编译器版本和选项。导入 CSS、图片和字体均参与缓存有效性判断。
- 目录刷新复核全部依赖元数据和实际路径；依赖变化、缺少文件、符号链接变化、安装替换、显式 reload 或编译规则变化时使缓存失效。文件监听事件主动使相关条目失效，显式 reload 始终重新读取依赖。
- 缓存命中时复用编译文本及资源编码。缓存按编译文本体积采用 64 MiB 的 LRU 上限，超出后释放未使用条目；当前正在使用的主题保持引用。Mode 和字号变化不重新编译 CSS。
- `InstalledExtension.themes` 的 CSS 分支只传递描述信息，合法但已停用的主题同样只包含描述。renderer 通过宿主 bridge 的 `getThemeStyle({ themeId, styleId })` 获取当前选中主题的完整文本，main 核对主题仍然启用、校验通过且内容标识一致。
- renderer 复用当前 `styleId` 的文本；切换请求使用递增序号，过期获取结果不能覆盖新的主题选择。主题切换完成后释放不再使用的文本。

#### extension 消息

- 每个连接的 `welcome.theme` 是完整 `ThemeInfo`，另带 `themeStyleId`。CSS 主题首次连接包含完整 `cssText`；JSON 和 Default 的 `themeStyleId` 为 `null`。
- `theme.changed` 携带完整主题元数据及明确的样式操作：`unchanged` 保留当前文本，`replace` 携带新的 `styleId` 与完整 `cssText`，`clear` 删除已有 CSS。消息格式见接口文档。
- broker 按连接记录已发送的 `styleId`。Mode、字号、名称或公共变量变化且样式内容相同时发送 `unchanged`；CSS 内容变化时发送 `replace`；转入 JSON 或 Default 时发送 `clear`。重新连接重新发送完整快照。
- SDK 在触发公开事件之前完成状态合并；`unchanged` 的标识必须与已有状态一致，否则报告协议错误。字段省略不承担保留或清除样式的含义。
- background 连接保持主题读取和事件能力，遵循相同的快照及增量规则；调用 `theme.apply()` 才解析并应用主题 CSS。各 WebContentsView 进程分别保存当前主题文本和样式表，字体及图片编码体积计入主题预算。

### 7. 切换、清理与布局同步

- 每次切换主题，在同一次样式更新中清除旧主题的变量和样式，并应用新主题；沿用现有的主题切换过渡处理。
- CSS 主题切换 Mode 时复用样式表，由实际的 `dark` class 选择深浅色规则。通过 `MutationObserver` 监听根元素的 class 属性；class 变化、主题内容变化、JSON 变量变化、字号变化及字体加载完成均调度下一帧的状态读取，多个触发合并为一次读取。
- 状态读取等待 `next-themes` 完成 class 更新，按照根元素当前 class 确定 `kind`。随后读取 `--agw-workspace-backdrop` 的计算值，判断其有效背景值并更新 `data-workspace-backdrop`，再读取公共变量、页面背景色并发布主题。Light、Dark 和 System 使用同一顺序，CSS 文本不变也重新执行。
- CSS、JSON 和 Default 相互切换时，在一次同步提交中更新主题系统拥有的样式表及变量，保留其他代码拥有的样式表和属性。已准备的新样式替换当前样式，异步获取期间继续显示当前主题。
- 停用、删除或校验失败导致主题不可用时，沿用现有主题选择处理，清除对应样式并使用 Default。
- CSS 主题启用期间，为已挂载的占位区域运行统一的 `requestAnimationFrame` 测量循环，包括暂时被 CSS 隐藏的区域。每帧读取区域和有效可见性，仅在结果变化时发送视图更新；因此位置变化、`:hover`、`:has()`、`:focus-within`、过渡及动画也能触发更新。
- 保留 `ResizeObserver`、窗口 resize、主题与 Mode 更新及字体加载通知，统一调度测量。宿主 document 隐藏时暂停帧循环，重新显示时立即测量；CSS 主题移除、占位区域注销或 provider 卸载时停止对应采样。
- 可见性判断包括元素连接状态、非空区域及 `checkVisibility({ visibilityProperty: true, opacityProperty: true, contentVisibilityAuto: true })`，同时检查占位元素自身的 `content-visibility: hidden`，参考 [CSSOM View](https://drafts.csswg.org/cssom-view/#dom-element-checkvisibility)。`display: none`、`visibility: hidden`、有效透明度为零或没有可见布局区域时隐藏原生视图，重新显示后恢复。
- `WebContentsView` 同步宿主内容区域坐标系下的轴向矩形位置、尺寸和可见性，沿用 renderer 与原生视图之间的坐标转换。占位区域及祖先上的圆角、部分透明、filter、clip-path、旋转和缩放不会转换为原生视图的绘制效果；DOM 的 z-index、遮挡和重叠也不决定原生视图层次。这些 CSS 可以作用于宿主 DOM，主题布局需要遵守该原生视图范围。
- 更新主题通过现有页面和 frame 完成，保留当前文档会话与未保存内容。

### 8. 校验与发布资料

- manifest 校验完成后，根据主题入口格式读取并校验文件。CSS 入口及其依赖使用同一套资源范围检查。
- 失败结果使用 `ExtensionValidationError`。字段路径指向对应的 `contributes.themes[index].path`，错误信息包含相关包内文件路径和可获得的行号、列号。
- PostCSS 的 `CssSyntaxError` 表示结构解析错误；资源表达式无法检查、资源类型不允许、路径不合法和超出预算分别报告对应原因。浏览器对选择器、属性名称和值的语义支持由 Chromium 判断，通过结构与资源检查的未知或不支持声明遵循浏览器处理规则。
- 任何主题文件校验失败时，所属 extension 按现有方式显示为不合法，其主题不出现在可用主题列表中。
- 安装校验、目录读取和内置 extension 构建共用主题加载逻辑，内置 CSS 主题处理失败时构建失败。
- 更新 Desktop README、SDK 类型说明与示例，说明 CSS 入口、Mode 选择器、默认变量来源、字号顺序、资源类型与上限、document 标记、内置 extension 的接入范围及原生视图限制。

## 接口文档

### 1. manifest 主题声明

`manifestVersion` 保持为 1，`contributes.themes[].path` 增加 CSS 文件支持：

```json
{
  "contributes": {
    "themes": [
      { "id": "custom", "title": "Custom", "path": "themes/custom.css" }
    ]
  }
}
```

CSS 文件示例：

```css
@import "./components.css";

:root {
  --custom-workspace-gap: 20px;
}

:root:not(.dark) {
  --background: #fffdf8;
  --foreground: #24211d;
}

:root.dark {
  --background: #24211d;
  --foreground: #fffdf8;
}

:root[data-agw-document="desktop"] .agw-main-workspace {
  gap: var(--custom-workspace-gap);
}

:root[data-agw-document="desktop"] .agw-extension-view-header {
  min-height: 40px;
}

:root[data-agw-document="extension"] body {
  color: var(--foreground);
}
```

### 2. ThemeInfo

`ThemeInfo` 增加可选字段：

```typescript
export interface ThemeInfo {
  id: string;
  title: string;
  kind: "light" | "dark";
  tokens: Record<string, string>;
  cssText?: string;
}
```

- `cssText` 用于完整快照和 SDK 公开状态。SDK 合并增量消息后，`theme.get()`、`theme.onDidChange` 的 CSS 主题状态均包含当前完整文本。
- `tokens` 为宿主已公布的公共变量的计算值；CSS 主题在各 document 中根据默认变量样式表与主题 CSS 计算，字号按照详细设计应用。
- `theme.apply(): Disposable` 的调用方式保持一致，同时接入变量、深浅色和可选的 CSS 样式。

### 3. 内部样式传输

`welcome` 保留完整的 `theme`，增加 `themeStyleId: string | null`。`theme.changed` 的内部消息格式为：

```typescript
type ThemeStyleUpdate =
  | { operation: "unchanged"; styleId: string | null }
  | { operation: "replace"; styleId: string; cssText: string }
  | { operation: "clear"; styleId: null };

interface ThemeChangedNotification {
  theme: Omit<ThemeInfo, "cssText">;
  style: ThemeStyleUpdate;
}
```

- `replace` 提供完整 CSS，`clear` 对应 JSON 或 Default，`unchanged` 复用该连接已经收到的内容。
- Desktop 宿主 bridge 增加 `getThemeStyle({ themeId, styleId })`，返回 `{ styleId, cssText }`。`themeId` 使用现有 `"{extensionId}:{themeId}"` 格式；请求结果必须匹配当前已校验的主题描述。
- 编译和资源预算错误使用现有 `ExtensionValidationError`，超限错误码为 `THEME_SIZE_LIMIT_EXCEEDED`。

## 测试计划

### 单元测试

- manifest：JSON 和 CSS 入口均通过校验，其他文件格式与不合法包内路径返回错误。
- CSS 加载：使用实际主题文件验证任意自定义变量、组件样式、Grid/Flex、深浅色规则、媒体查询、`@supports`、`@layer` 和动画。
- 包内依赖：覆盖重复导入、嵌套导入、循环导入、media、supports、命名及匿名 layer、各来源目录下的相对路径、包内根路径、图片、字体、data URL 和 document 片段引用；确认残留导入和未解析的路径返回错误。
- 资源检查：覆盖自定义属性中的外部 URL、escape URL、`image-set()` 与前缀形式的字符串地址、`image-set(var(--s) 1x)`、渐变候选项及无法检查的表达式；同时检查来源文件和最终文本。
- 文件类型：覆盖允许的图片与字体 MIME、未知扩展名、不允许的 data MIME、字体位置的图片资源、SVG 的外部引用与内嵌 CSS、namespace 标识符及 namespace 与导入的组合。
- 失败路径：覆盖结构解析失败、缺少导入或资源文件、路径超出包目录、符号链接指向包外和外部资源引用，确认错误包含来源信息。
- 体积限制：覆盖每项上限的边界、重复导入造成的展开增长、base64 后的输出大小及 data URL 解码体积，验证超限错误码。
- 缓存：覆盖所有依赖未变时命中、导入或资源变化、路径目标变化、依赖删除、安装替换、显式 reload 和 LRU 释放；Mode 与字号变化复用编译结果。
- 默认变量提取：验证 `--spacing`、`--text-sm`、`--color-red-500` 及变量依赖，保留 Mode、条件和 layer；分别检查名称清单及变量规则。
- 宿主应用：验证 CSS、JSON、Default 切换后的样式表与变量，仅移除主题拥有的资源，保留其他 adopted stylesheet。
- SDK：验证首次应用、重复调用、CSS 内容更新、Mode 更新、旧变量清理、字号 inline 属性、document 标记和停止订阅行为。
- 状态推送：验证完整 welcome、`unchanged`、`replace`、`clear`，同一主题 id 的内容更新、重新连接、background 状态、过期获取结果及错误的 `styleId`；公开事件始终获得完整状态。
- 受控确认：验证请求与 sender、一次性标识、窗口及包权限数据绑定；关闭或失效时取消，普通 renderer 消息不能形成批准结果。
- JSON 回归：保留现有主题变量清单、值类型、深浅色合并与内置主题校验测试。

### 集成测试

使用真实 Electron 与 CSS 主题 fixture，通过计算样式、字体加载结果和视图区域数值验证：

- 安装 CSS 主题后可以在 Appearance 中选择，宿主的自定义变量、组件样式及 Grid/Flex 布局生效。
- 已打开且调用 `theme.apply()` 的 extension 页面收到相同 CSS，自己的组件和布局随主题更新；未接入主题的页面保持原有样式。
- 验证宿主与 extension 的默认间距、字号变量、色阶变量及变量依赖；Appearance 设置字号后，Text Editor 的 code、preview、settings 页面继续使用对应字号。
- 验证通用规则对 Text Editor 和 Excalidraw 中匹配 DOM 的作用，以及 document 和 extension id 选择器的范围。
- 在主题应用后动态加入 Monaco 样式、普通 `<style>` 和 `<link>`，验证同等特异性的来源顺序；另外验证更高特异性、inline、layer 和 `!important` 的标准行为。
- 嵌套导入中的图片和字体可以离线加载，资源引用不产生外部请求。
- 使用真实 HTTP 请求计数验证外部资源用例在应用前被拒绝，SVG 图片内的外部引用同样不产生请求；namespace URI 可以通过校验且不触发请求。
- Light、Dark、System 变化时验证 `:root`、`:root:not(.dark)`、`:root.dark`；CSS 文本不变且背景只由深色规则定义时，公共变量、`kind` 和 `data-workspace-backdrop` 仍保持一致。
- CSS、JSON、Default 之间切换，以及主题 extension 停用、删除后的变量和样式表清理正确。
- 覆盖尺寸变化、仅位置变化、hover、focus、`:has()`、动画和字体加载，验证 `WebContentsView` 区域与占位区域一致；覆盖 `visibility: hidden`、透明度为零及重新显示，确认隐藏期间不接收输入。
- 验证宿主隐藏和恢复、占位区域注销及主题移除时的测量生命周期，确认没有无变化的重复视图 IPC。
- 使用尝试隐藏权限列表、改变按钮文字和排列顺序、覆盖整个宿主的 CSS，触发各类确认及权限批准，验证独立窗口的内容、交互与实际批准结果。
- 主题切换过程中 frame、文档会话和未保存内容保持，重新启动 Desktop 后主题选择和 Mode 保持。
- 不合法的 CSS 主题显示校验错误，其主题不出现在可用主题列表中。

### 回归测试

- 运行 Desktop 与 SDK 的 `test`、`typecheck`、`lint`，执行 Desktop 构建及客户端 `test:boundaries`。
- 通过相关组件和集成测试后，运行 Desktop 主流程，检查主题选择、切换、文件编辑和原生视图布局。
- 完成 `test:extensions`，验证 Default、Soft、Warm Minimalism、Text Editor 和现有 extension 生命周期。
- 检查 Electron main 的构建产物能够包含新增 CSS 处理依赖，安装后的应用可以读取和使用 CSS 主题。
