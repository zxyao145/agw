import assert from "node:assert/strict";
import test from "node:test";

import { createExtensionError } from "@agw/extension-sdk";

import { documentErrorMessage, errorCode } from "./errors";

test("errorCode returns the code of an Extension API error and null otherwise", () => {
  assert.equal(errorCode(createExtensionError("ERR_NOT_TEXT", "Binary.")), "ERR_NOT_TEXT");
  assert.equal(errorCode(new Error("Plain.")), null);
  assert.equal(errorCode("ERR_NOT_TEXT"), null);
});

test("documentErrorMessage explains binary files and unknown encodings and shows other messages", () => {
  assert.equal(
    documentErrorMessage(createExtensionError("ERR_NOT_TEXT", "The file contains NUL bytes.")),
    "This file is binary, so it can't be edited as text.",
  );
  assert.equal(
    documentErrorMessage(createExtensionError("ERR_UNKNOWN_ENCODING", "Unknown encoding.")),
    "The encoding of this file couldn't be detected.",
  );
  assert.equal(
    documentErrorMessage(createExtensionError("ENOENT", "notes.md does not exist.")),
    "notes.md does not exist.",
  );
  assert.equal(documentErrorMessage(new Error("Read failed.")), "Read failed.");
  assert.equal(documentErrorMessage("Read failed."), "Read failed.");
});
