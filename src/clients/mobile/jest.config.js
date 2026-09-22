module.exports = {
  preset: "jest-expo",
  roots: ["<rootDir>/tests"],
  setupFilesAfterEnv: ["<rootDir>/tests/setup.ts"],
  testMatch: ["**/*.test.ts", "**/*.test.tsx"],
};
