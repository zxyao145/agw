import type { ConfigContext, ExpoConfig } from "expo/config";

const appVersion = process.env.AGW_APP_VERSION ?? "0.1.0";
const buildNumber = process.env.AGW_BUILD_NUMBER ?? "1";

export default ({ config }: ConfigContext): ExpoConfig => ({
  ...config,
  name: "Agw",
  slug: "agw",
  version: appVersion,
  platforms: ["ios", "android"],
  scheme: "agw",
  orientation: "default",
  userInterfaceStyle: "light",
  icon: "./assets/agw-logo@2x.png",
  ios: {
    bundleIdentifier: "com.agw",
    buildNumber,
    supportsTablet: true,
    config: { usesNonExemptEncryption: false },
    infoPlist: {
      NSAppTransportSecurity: { NSAllowsArbitraryLoads: true },
    },
  },
  android: {
    package: "com.agw",
    versionCode: Number(buildNumber),
    usesCleartextTraffic: true,
    softwareKeyboardLayoutMode: "resize",
    adaptiveIcon: {
      foregroundImage: "./assets/agw-logo@2x.png",
      backgroundColor: "#FAF9FE",
    },
  },
  plugins: [
    "expo-router",
    [
      "expo-splash-screen",
      {
        backgroundColor: "#FAF9FE",
        image: "./assets/agw-logo@2x.png",
        imageWidth: 88,
      },
    ],
    [
      "expo-secure-store",
      {
        configureAndroidBackup: true,
      },
    ],
    [
      "expo-image-picker",
      {
        photosPermission: "Allow Agw to select images to send in a conversation.",
        cameraPermission: false,
        microphonePermission: false,
      },
    ],
  ],
  experiments: {
    typedRoutes: true,
    reactCompiler: true,
  },
});
