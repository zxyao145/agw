const path = require("path");
const { getDefaultConfig } = require("expo/metro-config");
const { executionCoreSource } = require("./execution-core-alias.cjs");

const config = getDefaultConfig(__dirname);

config.resolver = {
  ...config.resolver,
  extraNodeModules: {
    ...(config.resolver?.extraNodeModules ?? {}),
    "prop-types": path.resolve(__dirname, "src/rn/shims/prop-types"),
    "@agw/execution-core": executionCoreSource,
  },
};

config.watchFolders = [...(config.watchFolders ?? []), executionCoreSource];

module.exports = config;
