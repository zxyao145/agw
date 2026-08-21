module.exports = {
  preset: "jest-expo",
  roots: ["<rootDir>/tests"],
  moduleDirectories: ["node_modules", "<rootDir>/node_modules"],
  setupFilesAfterEnv: ["<rootDir>/tests/setup.ts"],
  testMatch: ["**/*.test.ts", "**/*.test.tsx"],
};
