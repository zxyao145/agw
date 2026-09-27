module.exports = {
  preset: "jest-expo",
  roots: ["<rootDir>/tests"],
  setupFilesAfterEnv: ["<rootDir>/tests/setup.ts"],
  testMatch: ["**/*.test.ts", "**/*.test.tsx"],
  // jest-expo 默认列表加上只发布 ESM 的 uuid，让 babel-jest 把它转成 CommonJS。
  // jest-expo defaults plus the ESM-only uuid package, so babel-jest compiles it to CommonJS.
  transformIgnorePatterns: [
    "/node_modules/(?!(.pnpm|react-native|@react-native|@react-native-community|expo|@expo|@expo-google-fonts|react-navigation|@react-navigation|@sentry/react-native|native-base|standard-navigation|uuid))",
    "/node_modules/react-native-reanimated/plugin/",
    "/node_modules/@react-native/babel-preset/",
  ],
};
