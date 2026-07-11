const path = require("path");
const { getDefaultConfig } = require("expo/metro-config");

const config = getDefaultConfig(__dirname);

config.resolver = {
  ...config.resolver,
  extraNodeModules: {
    ...(config.resolver?.extraNodeModules ?? {}),
    "prop-types": path.resolve(__dirname, "src/rn/shims/prop-types"),
  },
};

module.exports = config;
