# Extension id 使用 GUID v7

## 名词解释

- **extension id**：manifest 的 `id` 字段。Desktop 用它命名安装目录（`<userData>/extensions/packages/<id>`）、数据目录（`<userData>/extensions/data/<id>`）、session（`persist:agw-extension-<id>`）与页面地址（`agw-extension://<id>/`），视图 id 与主题 id 以 `<id>:` 开头；Server 用它记录对话的创建方（`project_conversation.extension_id`）。
- **GUID v7**：RFC 9562 定义的 version 7 UUID。前 48 位是 Unix 毫秒时间戳，小写字符串的字典序与生成顺序一致。
- 其余名词沿用 `2026-10-16-Desktop-Extension.md`。

## 背景

- extension id 由作者自己命名，取值是 3 到 64 个小写字母、数字与单个连字符。不同作者容易取到同一个名称，用户安装的 extension 与内置 extension 同 id 时会替换内置 extension。
- 名称形式的 id 需要单独排除保留值：Desktop 排除 `agw` 开头的 id 与 `data`、`packages`、`host`，Server 排除 `host`。

## 设计目标

### 功能目标

1. 所有 extension 的 id 都是小写的 GUID v7。Desktop 的 manifest 校验、IPC 参数校验、主题 id 校验与 Server 的 `SettingCommand.extensionId` 校验只接受这种格式。
2. 内置 extension 与集成测试 fixture 使用新生成的 GUID v7，源码目录名与 npm 包名随 id 改变。
3. 界面中显示 id 的位置（Extensions 卡片、确认对话框、会话列表的创建方）照常显示 id。

### 技术目标

- **一致**：Desktop 与 Server 对同一个字符串给出相同的校验结果：version 为 7，variant 为 RFC 9562（`8`、`9`、`a`、`b`），36 个字符的连字符格式，全部小写。
- **排序**：按 id 排序的位置（Extensions 列表、文件查看器、主题）不需要修改。新 id 按原名称的字母顺序生成，内置 extension 与 fixture 之间的相对顺序保持不变，依赖顺序的集成检查不受影响。
- **兼容**：extension 功能还没有发布（main 与各个 tag 中都没有），不迁移旧 id 的设置与数据。开发机上旧 id 的数据目录与 `extensions`、`removedBundledExtensions`、`fileViewerDefaults` 中的条目不再对应任何 extension；`appearance.theme` 为旧主题 id 时 `settings.json` 读取失败，需要手工改为 `default` 或新 id 的主题。

## 详细设计

### 1. Desktop 校验

- `desktop/src/shared/extension-manifest.ts` 导出 `extensionIdSchema = z.uuidv7().regex(/^[^A-Z]*$/u, ...)` 与 `isExtensionId(value)`。zod 的 `uuidv7()` 校验 version 与 variant，但不区分大小写，`regex` 补充小写要求；`toJSONSchema` 把两项输出为 `allOf` 中的两个 `pattern`，`manifest.schema.json` 做同样的检查。
- manifest 的 `id` 使用 `extensionIdSchema`。GUID v7 的长度固定，也不会等于 `data`、`packages`、`host` 或以 `agw` 开头，`id` 没有单独的长度与保留值检查。`name` 不能为 `host`。
- `desktop/src/main/extensions/extension-ipc.ts` 的 `assertExtensionId` 使用 `isExtensionId`。
- `desktop/src/main/settings/settings-store.ts` 的 `appearance.theme` 为 `default` 或 `<extensionId>:<themeId>`：用第一个 `:` 分开，前一部分满足 `isExtensionId`，后一部分满足主题 id 的格式（与 manifest 中 `contributes.themes[].id` 相同的 `LOCAL_ID_PATTERN`，由 `extension-manifest.ts` 导出 `isLocalId`）。

### 2. Server 校验

- `SettingCommandMapper.ValidateExtensionId`：`null` 表示宿主；其他值用 `Guid.TryParseExact(value, "D", out var guid)` 解析，并要求 `guid.Version == 7`、`(guid.Variant & 0xC) == 0x8`、`guid.ToString("D") == value`（`ToString("D")` 输出小写，相等即为小写的规范形式），否则以 `InvalidParam`（4000001）拒绝，消息为 `SettingCommand.extensionId must be a lowercase GUID v7.`。`host` 无法通过解析，不需要单独判断。
- `ProjectConversation.ExtensionId`（最大长度 64）与 `ConversationCreators.Host` 不变，不需要迁移。

### 3. 内置 extension

新 id 如下，源码目录为 `src/clients/packages/extensions/<id>`，包名为 `@agw/extension-<id>`：

- Excalidraw：`01a173b5-526c-74d8-864c-0e2da2d0a21c`
- Image Viewer：`01a173b5-526c-74d8-864c-116f66ff919e`
- Soft Theme：`01a173b5-526c-74d8-864c-26b0968892f6`
- Markdown：`01a173b5-526c-74d8-864c-2fa2035185e4`
- Warm Minimalism Theme：`01a173b5-526c-74d8-864c-3fd14f2c28b8`

`build-bundled-extensions.mjs` 仍要求 manifest id 等于源码目录名，输出到 `resources/extensions/<id>`。修改目录名之后运行 `pnpm install` 更新 `pnpm-lock.yaml` 的 importers。

### 4. 测试 fixture

- `desktop/test-fixtures/extensions-src/<id>` 使用新 id 命名，manifest 的 `id` 同步修改：bad-theme `01a173b5-526c-74d8-864b-f2dd23048b02`、bad-view `01a173b5-526c-74d8-864b-f4f8799cd3e7`、blazor `01a173b5-526c-74d8-864b-f827a185467f`、broken `01a173b5-526c-74d8-864b-ff32a943d13f`、busy `01a173b5-526c-74d8-864c-0076f8ed192f`、contrast-theme `01a173b5-526c-74d8-864c-047bc35e649f`、css-theme `01a173b5-526c-74d8-864c-0a09dd98b184`、no-background `01a173b5-526c-74d8-864c-169e27458b07`、react-ts `01a173b5-526c-74d8-864c-1b219cd2c29c`、sample `01a173b5-526c-74d8-864c-1effa3c5a7ce`、slow `01a173b5-526c-74d8-864c-22d6a1aaef33`、stuck-deactivate `01a173b5-526c-74d8-864c-2bb9cedcb314`、todo-editor `01a173b5-526c-74d8-864c-3331af0b584a`、undeclared `01a173b5-526c-74d8-864c-36df2797b3fc`、vue `01a173b5-526c-74d8-864c-39000bf7bd08`、wasm `01a173b5-526c-74d8-864c-43435b944250`。
- `desktop/test-fixtures/extension-ids.json` 记录内置 extension 与 fixture 的 id，键为测试代码使用的名称（`markdown`、`excalidraw`、`sample`、`todoEditor` 等）。`build-extension-fixtures.mjs` 与 `test-extensions.mjs` 用 JSON import（`with { type: "json" }`）读取；`extensions.integration.ts` 由 esbuild 打包，同样 import 这个文件，测试代码通过这些名称引用 id（如 `EXTENSION_IDS.markdown`）。`desktop/tsconfig.json` 开启 `resolveJsonModule`，类型检查覆盖这些引用。
- 集成测试中由 id 组成的值都从 `EXTENSION_IDS` 生成：选择器 `[data-extension-id="${id}"]`、视图 id 与 Ribbon 项 id `${id}:app`、主题 id、`agw-extension://${id}/` 地址与存储目录。fixture 页面中的文本、CSS 类名与文件名不是 id，保持不变。
- `extension-network.integration.ts` 在运行时写入的 manifest 使用一个固定的 GUID v7。

### 5. 宿主代码

- `host-extension-ids.test.ts` 检查宿主代码中不出现内置 extension 的 id，列表改为上面 5 个 GUID。
- 视图 id、主题 id 按第一个 `:` 分开，GUID 中没有 `:`，解析代码不需要修改。

### 6. 文档

- `CONTEXT.md`、`docs/ws-flow.md`、`src/server/Agw.Agents.Execution/README.md`、`src/clients/desktop/README.md` 中 id 的格式说明改为小写的 GUID v7。

## 测试计划

### 单元测试

- `extension-manifest.test.ts`：小写 GUID v7 合法；大写 GUID、GUID v4、名称形式的 id、`host` 为 `INVALID_FIELD`；生成的 JSON Schema 对同样的输入给出相同的结果。
- `settings-store` 的测试：`default` 与 `<GUID v7>:<themeId>` 合法；名称形式的 extension id 读取失败。
- 其他构造 manifest 并经过校验的 Desktop 单元测试改用 GUID v7（`extension-catalog`、`extension-installer`、`extension-protocol`）；`extension-theme-file.test.ts` 通过 `extension-ids.json` 找到内置主题包的目录。渲染进程中没有经过 manifest 校验的单元测试数据不变。
- 内置包自己的测试（Markdown 的 `test-host.ts` 与存储路径、Excalidraw 的 `error-message.test.ts`）使用各自的新 id。
- Server：`ExecutionRequestsTests` 覆盖小写 GUID v7 通过；大写、GUID v4、`host`、名称形式的值以 `InvalidParam` 拒绝；其他经过 `SettingCommandMapper` 的测试改用 GUID v7。

### 集成测试

- `pnpm --filter @agw/desktop test:extensions` 的 first 与 restart 阶段全部通过。
