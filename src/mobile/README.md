# Agw Mobile

本目录包含 Agw 的 Expo 移动应用。`shared/` 是 Expo app 根目录，React Native 代码位于 `shared/src/rn/`。

旧的顶层 SwiftUI/Kotlin 原生壳已废弃。需要原生工程时，通过 Expo prebuild 从 `shared/app.json` 重新生成 `shared/ios/` 和 `shared/android/`。

## 环境要求

- Node.js `>= 22.11.0`
- npm
- Android Studio 或 Android SDK
- Android Emulator 或真机，且 `adb` 可用
- macOS + Xcode 运行 iOS 构建时需要

## 首次安装

```sh
cd shared
npm ci
```

## 本地开发

启动 Expo：

```sh
cd shared
npm start
```

运行 Android development build：

```sh
cd shared
npm run android
```

运行 iOS development build：

```sh
cd shared
npm run ios
```

生成原生工程：

```sh
cd shared
npm run prebuild
```

`shared/android/` 和 `shared/ios/` 是 Expo prebuild 输出，默认不作为手写源码维护。优先通过 `shared/app.json`、Expo config plugins 和 npm 依赖表达原生配置。

## React Native 代码

常用文件：

- `shared/index.js`：Expo 入口，使用 `registerRootComponent(App)`。
- `shared/app.json`：Expo app config，包含 bundle id、Android package 和插件配置。
- `shared/src/rn/App.tsx`：React Native 页面入口。
- `shared/src/rn/routes.ts`：页面 route 定义。
- `shared/src/rn/config/config-store.ts`：通过 `expo-secure-store` 保存本地配置。

本地配置中的 API key 存在 Expo SecureStore 中。旧安装里的文件配置路径不再读取，也不做迁移；用户需要重新导入或保存配置。

## 测试

```sh
cd shared
npm test
npm run typecheck
npx expo config
npx expo install --check
```

Jest 测试位于 `shared/__tests__/`，文件名使用 `*.test.ts`、`*.test.tsx` 或 `*.test.js`。

## 常见问题

如果依赖版本与 Expo SDK 不匹配：

```sh
cd shared
npx expo install --check
npx expo install --fix
```

如果需要重新生成原生目录：

```sh
cd shared
npm run prebuild -- --clean
```

如果 Android 设备无法连接本机开发服务：

```sh
adb reverse tcp:8081 tcp:8081
```
