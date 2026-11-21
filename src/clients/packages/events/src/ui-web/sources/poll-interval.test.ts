import assert from "node:assert/strict";
import test from "node:test";

import { fromIsoInterval, toIsoInterval } from "./poll-interval";

test("poll intervals convert between number, unit and ISO duration", () => {
  assert.equal(toIsoInterval(5, "minutes"), "PT5M");
  assert.equal(toIsoInterval(2, "days"), "P2D");
  assert.deepEqual(fromIsoInterval("PT2H"), { value: 2, unit: "hours" });
  assert.equal(fromIsoInterval("PT90S"), null);
  assert.equal(fromIsoInterval("PT1M1S"), null);
  assert.equal(fromIsoInterval("P1M"), null);
  assert.equal(fromIsoInterval("PT1.5M"), null);
  assert.equal(fromIsoInterval("PT1,5H5M"), null);
  assert.equal(fromIsoInterval("P"), null);
  assert.equal(fromIsoInterval("PT"), null);
});
