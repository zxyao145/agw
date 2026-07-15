import assert from "node:assert/strict";
import test from "node:test";

import { createDefaultConnectionAlias, isConnectionAliasValid } from "./connection-alias.ts";

test("isConnectionAliasValid accepts lowercase kebab-case aliases", () => {
  for (const alias of ["github", "github-account", "github-2-work"]) {
    assert.equal(isConnectionAliasValid(alias), true, alias);
  }
});

test("isConnectionAliasValid rejects aliases outside the server contract", () => {
  for (const alias of [
    "",
    "github_account",
    "GitHub-account",
    "github--account",
    "-github",
    "github-",
    " github-account ",
    `a${"b".repeat(128)}`,
  ]) {
    assert.equal(isConnectionAliasValid(alias), false, alias);
  }
});

test("createDefaultConnectionAlias creates a lowercase kebab-case account alias", () => {
  assert.equal(createDefaultConnectionAlias("GitHub"), "github-account");
});
