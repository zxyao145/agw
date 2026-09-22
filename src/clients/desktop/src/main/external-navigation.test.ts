import assert from "node:assert/strict";
import test from "node:test";

import { getExternalHttpUrl } from "./external-navigation";

test("getExternalHttpUrl accepts HTTP and HTTPS links", () => {
  assert.equal(
    getExternalHttpUrl("https://example.com/docs?q=agw"),
    "https://example.com/docs?q=agw",
  );
  assert.equal(getExternalHttpUrl("http://example.com"), "http://example.com/");
});

test("getExternalHttpUrl rejects malformed and non-web links", () => {
  assert.equal(getExternalHttpUrl("not a URL"), null);
  assert.equal(getExternalHttpUrl("agw://app/desktop/chat/"), null);
  assert.equal(getExternalHttpUrl("file:///Users/example/readme.md"), null);
  assert.equal(getExternalHttpUrl("javascript:alert(1)"), null);
});
