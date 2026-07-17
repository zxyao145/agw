import assert from "node:assert/strict";
import test from "node:test";

import {
  DEFAULT_PAGE_SIZE,
  getClampedPageIndex,
  getPaginationMeta,
  PAGE_SIZE_OPTIONS,
} from "./pagination.ts";

test("pagination exposes the supported page sizes and defaults to 20", () => {
  assert.deepEqual(PAGE_SIZE_OPTIONS, [10, 20, 50]);
  assert.equal(DEFAULT_PAGE_SIZE, 20);
});

test("getPaginationMeta describes a populated middle page", () => {
  assert.deepEqual(getPaginationMeta(41, 2, 20), {
    start: 21,
    end: 40,
    totalPages: 3,
    canGoPrevious: true,
    canGoNext: true,
  });
});

test("getPaginationMeta describes an empty first page", () => {
  assert.deepEqual(getPaginationMeta(0, 1, 20), {
    start: 0,
    end: 0,
    totalPages: 1,
    canGoPrevious: false,
    canGoNext: false,
  });
});

test("getClampedPageIndex moves an out-of-range page to the last valid page", () => {
  assert.equal(getClampedPageIndex(20, 3, 10), 2);
  assert.equal(getClampedPageIndex(0, 2, 10), 1);
  assert.equal(getClampedPageIndex(21, 2, 10), 2);
});
