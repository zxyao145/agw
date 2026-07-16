import assert from "node:assert/strict";
import test from "node:test";

interface AutoScrollState {
  shouldAutoScroll: boolean;
  scrollHeight: number;
  scrollTop: number;
}

interface ScrollMetrics {
  clientHeight: number;
  scrollHeight: number;
  scrollTop: number;
}

type UpdateAutoScrollState = (state: AutoScrollState, metrics: ScrollMetrics) => AutoScrollState;

let updateAutoScrollState: UpdateAutoScrollState | undefined;

try {
  // @ts-expect-error Node's type stripping requires the explicit TypeScript extension.
  const autoScroll = await import("./auto-scroll.ts");
  updateAutoScrollState = autoScroll.updateAutoScrollState;
} catch (error) {
  if (!(error instanceof Error) || !("code" in error) || error.code !== "ERR_MODULE_NOT_FOUND") {
    throw error;
  }
}

function getUpdateAutoScrollState(): UpdateAutoScrollState {
  assert.equal(typeof updateAutoScrollState, "function");
  return updateAutoScrollState;
}

test("auto scroll remains enabled when streamed content grows without an upward scroll", () => {
  const update = getUpdateAutoScrollState();

  assert.deepEqual(
    update(
      { shouldAutoScroll: true, scrollHeight: 1000, scrollTop: 500 },
      { clientHeight: 500, scrollHeight: 1080, scrollTop: 500 },
    ),
    { shouldAutoScroll: true, scrollHeight: 1080, scrollTop: 500 },
  );
});

test("auto scroll resumes when a downward scroll reaches the bottom from before content grew", () => {
  const update = getUpdateAutoScrollState();

  assert.deepEqual(
    update(
      { shouldAutoScroll: false, scrollHeight: 1000, scrollTop: 200 },
      { clientHeight: 500, scrollHeight: 1080, scrollTop: 500 },
    ),
    { shouldAutoScroll: true, scrollHeight: 1080, scrollTop: 500 },
  );
});

test("auto scroll pauses on upward movement and stays paused away from the bottom", () => {
  const update = getUpdateAutoScrollState();

  assert.equal(
    update(
      { shouldAutoScroll: true, scrollHeight: 1000, scrollTop: 500 },
      { clientHeight: 500, scrollHeight: 1020, scrollTop: 480 },
    ).shouldAutoScroll,
    false,
  );
  assert.equal(
    update(
      { shouldAutoScroll: false, scrollHeight: 1000, scrollTop: 200 },
      { clientHeight: 500, scrollHeight: 1200, scrollTop: 300 },
    ).shouldAutoScroll,
    false,
  );
  assert.equal(
    update(
      { shouldAutoScroll: false, scrollHeight: 1200, scrollTop: 300 },
      { clientHeight: 500, scrollHeight: 1200, scrollTop: 700 },
    ).shouldAutoScroll,
    true,
  );
});
