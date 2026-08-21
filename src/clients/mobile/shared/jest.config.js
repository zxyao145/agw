const { executionCoreEntry } = require('./execution-core-alias.cjs');

module.exports = {
  preset: 'jest-expo',
  moduleNameMapper: {
    '^@agw/execution-core$': executionCoreEntry,
    '^@babel/runtime/(.*)$': '<rootDir>/node_modules/@babel/runtime/$1',
  },
};
