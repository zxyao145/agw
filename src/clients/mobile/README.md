# Agw Mobile

Agw Mobile 是基于 Expo SDK 57、Expo Router 和 React Native 的 iOS/Android 客户端。应用根目录就是本目录，并已加入 `src/clients` 的 pnpm Workspace 与 Turborepo。

## 开发

在 `src/clients/` 运行：

```bash
pnpm install
pnpm dev:mobile
pnpm android:mobile
pnpm ios:mobile
pnpm --filter @agw/mobile typecheck
pnpm --filter @agw/mobile test
pnpm --filter @agw/mobile build
```

`android/` 和 `ios/` 由 Expo Continuous Native Generation 生成并被 Git 忽略。需要检查原生配置时运行：

```bash
pnpm --filter @agw/mobile native:generate -- --clean
```

## 连接 Agw Server

在 Agw Web 的 Settings 中创建独立 API Token，并复制 Base64URL Mobile 配置。Mobile Settings 支持导入该 v2 配置，也支持手工输入 Profile 名称、根 Server URL 和 token。

- Profile 元数据保存在 AsyncStorage。
- token 按 Profile 分别保存在 Expo SecureStore。
- 旧版 `agw.localConfig` 会在首次启动时自动迁移。
- HTTP 连接必须确认明文传输风险；公网使用仍建议 HTTPS。
- 删除 Mobile Profile 不会撤销服务端 token，撤销操作需在 Agw Web 完成。

## 共享代码边界

Mobile 通过 workspace 依赖复用 `@agw/api`、`@agw/execution-core`、`@agw/chat-core` 和 `@agw/projects-core`，不再维护独立 OpenAPI 副本或 Metro 源码别名，也不导入 Web UI。
