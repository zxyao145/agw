const path = require("path");

jest.mock("expo/metro-config", () => ({
  getDefaultConfig: () => ({ resolver: {} }),
}));

describe("metro config", () => {
  it("aliases prop-types to the local React Native shim", () => {
    const config = require("../metro.config");

    expect(config.resolver.extraNodeModules["prop-types"]).toBe(
      path.resolve(__dirname, "../src/rn/shims/prop-types")
    );
  });
});
