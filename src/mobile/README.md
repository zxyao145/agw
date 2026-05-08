# Agw

本仓库包含 iOS SwiftUI 和 Android 原生壳应用，二者都内嵌 `shared/` 下的 React Native 应用。

- iOS 工程：`ios/Agw.xcworkspace`
- Android 工程：`android/`
- SwiftUI 入口：`ios/Agw/ContentView.swift`
- React Native 宿主：`ios/Agw/ReactViewController.swift`
- Android React Native 入口：`android/app/src/main/java/com/agw/MainActivity.kt`
- React Native 应用：`shared/src/rn/App.tsx`
- React Native 路由：`shared/src/rn/routes.ts`

## 环境要求

- Xcode
- Android Studio 或 Android SDK
- Android Emulator 或真机，且 `adb` 可用
- JDK 17+
- Node.js `>= 22.11.0`
- npm
- Ruby + Bundler
- CocoaPods 通过 `ios/Gemfile` 管理

## 首次安装

先安装 React Native 依赖。推荐使用 lockfile 安装：

```sh
cd shared
npm ci
```

如果需要更新依赖版本，再使用 `npm install` 并提交更新后的 `package-lock.json`。

再安装 iOS 依赖：

```sh
cd ../ios
bundle install
bundle exec pod install
```

之后用 Xcode 打开 workspace：

```sh
open Agw.xcworkspace
```

## 本地开发启动

开发 React Native 页面时，先启动 Metro：

```sh
cd shared
npm start
```

然后启动 iOS App。可以二选一：

```sh
cd shared
npm run ios
```

或在 Xcode 中打开 `ios/Agw.xcworkspace`，选择 `Agw` scheme，运行到 iOS Simulator。

运行 Android App：

```sh
cd shared
npm run adb
npm run android
```

这条路径会调用 React Native CLI，使用 `shared/react-native.config.js` 找到 `../android` 工程，然后构建、安装并启动 debug App。

也可以从 Android 工程目录手动构建、安装和启动：

```powershell
cd android
.\gradlew.bat :app:installDebug
adb shell am start -n com.agw/.MainActivity
```

macOS/Linux 下使用：

```sh
cd android
./gradlew :app:installDebug
adb shell am start -n com.agw/.MainActivity
```

Debug 模式下，Swift 侧会从 Metro 加载 JS：

```swift
RCTBundleURLProvider.sharedSettings().jsBundleURL(forBundleRoot: "index")
```

Release 模式下会加载 App 内置的 `main.jsbundle`。Xcode 里的 `Bundle React Native code and images` build phase 会以 `shared/` 作为 React Native 项目根目录执行打包。

## iOS 开发

常用文件：

- `ios/Agw/AgwApp.swift`：SwiftUI App 入口。
- `ios/Agw/ContentView.swift`：顶层 UI，目前使用 `TabView` 显示 Home、Settings、Details。
- `ios/Agw/ReactNativePage.swift`：定义 Swift 侧可打开的 RN 页面、标题、tab 图标和初始参数。
- `ios/Agw/ReactNativeView.swift`：把 UIKit 的 RN 宿主控制器包装成 SwiftUI view。
- `ios/Agw/ReactViewController.swift`：创建 `RCTReactNativeFactory`，并把 Swift 侧 props 传给 RN。

新增一个顶级 RN 页面通常需要：

1. 在 `shared/src/rn/routes.ts` 中新增 route。
2. 在 `ios/Agw/ReactNativePage.swift` 的 `samples` 中新增对应 `ReactNativePage`。
3. 如果需要新参数，把 Swift 的 `initialProps` 和 TypeScript 的 props 类型一起更新。

如果改动了 Swift、Pods、Xcode 配置或原生依赖，需要重新 build iOS App。

## Android 开发

常用文件：

- `android/settings.gradle`：让 Gradle 从 `shared/node_modules` 加载 React Native Gradle plugin，并在 `shared/` 下执行 RN autolinking。
- `android/app/build.gradle`：Android app 配置，`react` block 显式指向 `../../shared`、`../../shared/index.js` 和 `shared/node_modules`。
- `android/app/src/main/java/com/agw/MainApplication.kt`：Android Application，加载 React Native runtime。
- `android/app/src/main/java/com/agw/MainActivity.kt`：Android Activity，加载 `AgwReactNative` 模块，并向 RN 传入默认 `routeName=home`、`title=Home`、`source=Android`。

Android 的顶层页面导航由 `shared/src/rn/App.tsx` 渲染。`source=Android` 时会显示底部 Home、Settings、Details tab；iOS 仍由 SwiftUI `TabView` 提供原生 tab。

常用 Android 命令：

```powershell
cd android
.\gradlew.bat :app:assembleDebug
.\gradlew.bat :app:installDebug
adb shell am start -n com.agw/.MainActivity
```

如果当前有多个设备，给 `adb` 指定设备：

```powershell
adb devices
adb -s emulator-5554 reverse tcp:8081 tcp:8081
adb -s emulator-5554 shell am start -n com.agw/.MainActivity
```

Android 原生代码可以用 Android Studio 打开 `android/` 开发。改动 Kotlin、Gradle、Manifest 或原生依赖后，需要重新 build Android App。

## React Native 开发

常用文件：

- `shared/index.js`：注册 RN 模块，模块名来自 `shared/app.json` 的 `AgwReactNative`。
- `shared/src/rn/App.tsx`：RN 页面入口，根据 native 传入的 props 渲染页面。`source=Android` 时渲染底部 tab；`source=SwiftUI` 时渲染单个 route。
- `shared/src/rn/routes.ts`：RN route 定义和 Android tab 顺序。
- `shared/metro.config.js`：Metro 配置。
- `shared/react-native.config.js`：让 RN CLI 知道 Android 工程在 `../android`、iOS 工程在 `../ios`。

开发 JS/TS 页面时保持 Metro 运行。多数 JS/TS 改动可以通过 Fast Refresh 生效；如果状态异常，可以在 Simulator 中重新加载 App，或重启 Metro。

常用命令：

```sh
cd shared
npm test
npm run typecheck
```

`npm run lint` 目前需要先补充 ESLint 配置，否则会因为找不到配置文件而失败。

如果 Metro 缓存异常：

```sh
cd shared
npm start -- --reset-cache
```

## 调试

### iOS 调试

- 在 Xcode 中给 Swift 文件打断点，例如 `ReactViewController.swift` 或 `ContentView.swift`。
- 用 Xcode console 查看原生日志、启动错误和 React Native bridge 相关输出。
- 如果改了 native 代码，重新 build 并运行 App。
- 如果新增或更新了 native dependency，先在 `shared/` 安装 npm 包，再在 `ios/` 执行 `bundle exec pod install`。

### React Native 调试

- Metro 必须在 `shared/` 目录启动。
- JS 报错会显示在 Simulator 的红屏或 Metro 终端中。
- iOS 页面收到的初始参数来自 `ReactNativePage.props`，包括 `routeName`、`title`、`source` 以及每个页面自己的 `initialProps`。
- Android 页面收到的初始参数来自 `MainActivity.kt` 的 `getLaunchOptions()`。
- 如果 RN 页面没有加载，先确认 Metro 运行在 `8081`，再确认当前 scheme 是 Debug。
- Android 默认从 `MainActivity.kt` 传入 `routeName=home`、`title=Home`、`source=Android`，RN 会据此显示 Android 底部 tab。

确认 Metro 状态：

```sh
curl http://localhost:8081/status
```

正常会返回：

```text
packager-status:running
```

### Android 调试

启动 Metro：

```sh
cd shared
npm start
```

确认设备或模拟器在线：

```sh
adb devices
```

让模拟器或真机访问本机 Metro：

```sh
cd shared
npm run adb
```

安装并启动 App：

```powershell
cd android
.\gradlew.bat :app:installDebug
adb shell am start -n com.agw/.MainActivity
```

查看前台 Activity：

```sh
adb shell dumpsys activity activities | grep com.agw
```

Windows PowerShell 可用：

```powershell
adb shell dumpsys activity activities | Select-String com.agw
```

查看 Android 和 React Native 日志：

```sh
adb logcat | grep -E "com.agw|ReactNativeJS|AndroidRuntime|FATAL EXCEPTION"
```

Windows PowerShell 可用：

```powershell
adb logcat | Select-String -SimpleMatch "com.agw","ReactNativeJS","AndroidRuntime","FATAL EXCEPTION"
```

重新启动 App：

```sh
adb shell am force-stop com.agw
adb shell am start -n com.agw/.MainActivity
```

### 常见问题

Metro 没启动或端口不可用：

```sh
cd shared
npm start -- --reset-cache
```

Pods 与 node_modules 不匹配：

```sh
cd shared
npm install
cd ../ios
bundle exec pod install
```

Xcode 打开了 `.xcodeproj` 而不是 workspace：

```sh
open ios/Agw.xcworkspace
```

Android 设备连不上 Metro：

```sh
cd shared
npm run adb
```

`adb reverse` 成功时会输出 `8081`。

Windows 下 Gradle 找不到 `npx`：

- 确认已在 `shared/` 执行 `npm ci`。
- 确认 `android/settings.gradle` 使用了 Windows 兼容的 `cmd /c npx ...` autolinking 命令。
- 重新执行 `cd android && .\gradlew.bat :app:assembleDebug`。

修改 RN 页面后模拟器没刷新：

```sh
adb shell input keyevent 82
```

在开发菜单中选择 Reload；或直接重启 App：

```sh
adb shell am force-stop com.agw
adb shell am start -n com.agw/.MainActivity
```

## 验证

React Native 测试：

```sh
cd shared
npm test
```

TypeScript 类型检查：

```sh
cd shared
npm run typecheck
```

iOS 编译检查：

```sh
xcodebuild -workspace ios/Agw.xcworkspace -scheme Agw -destination 'generic/platform=iOS Simulator' build
```

Android 编译检查：

```sh
cd android
./gradlew :app:assembleDebug
```

Windows PowerShell：

```powershell
cd android
.\gradlew.bat :app:assembleDebug
```
