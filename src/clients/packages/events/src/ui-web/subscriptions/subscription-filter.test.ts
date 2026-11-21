import assert from "node:assert/strict";
import test from "node:test";

import {
  filterRowsToJsonLogic,
  jsonLogicToFilterRows,
  validateFilterRows,
  type FilterRow,
} from "./subscription-filter";

test("scope rows convert to in", () => {
  assert.deepEqual(
    filterRowsToJsonLogic([{ field: "scope", dataPath: "", values: ["proj1", "proj2"] }]),
    {
      and: [{ in: [{ var: "scope" }, ["proj1", "proj2"]] }],
    },
  );
});

test("tags rows convert to some", () => {
  assert.deepEqual(filterRowsToJsonLogic([{ field: "tags", dataPath: "", values: ["x", "y"] }]), {
    and: [{ some: [{ var: "tags" }, { in: [{ var: "" }, ["x", "y"]] }] }],
  });
});

test("data rows compare text", () => {
  assert.deepEqual(
    filterRowsToJsonLogic([{ field: "data", dataPath: "status", values: ["1", "open"] }]),
    {
      and: [{ in: [{ cat: [{ var: "data.status" }] }, ["1", "open"]] }],
    },
  );
});

test("supported rows round trip and empty rows have no filter", () => {
  const rows: FilterRow[] = [
    { field: "subject", dataPath: "", values: ["a,b"] },
    { field: "scope", dataPath: "", values: ["proj1"] },
    { field: "tags", dataPath: "", values: ["x"] },
    { field: "data", dataPath: "pull_request.state", values: ["open"] },
  ];
  assert.deepEqual(jsonLogicToFilterRows(filterRowsToJsonLogic(rows)), rows);
  assert.equal(filterRowsToJsonLogic([]), null);
  assert.deepEqual(jsonLogicToFilterRows(null), []);
  assert.equal(jsonLogicToFilterRows({ "==": [{ var: "type" }, "a"] }), null);
});

test("invalid data paths and empty values cannot be saved", () => {
  for (const dataPath of ["", "a b", "a..b"]) {
    assert.ok(validateFilterRows([{ field: "data", dataPath, values: ["open"] }]).length > 0);
  }
  assert.ok(validateFilterRows([{ field: "scope", dataPath: "", values: [] }]).length > 0);
});
