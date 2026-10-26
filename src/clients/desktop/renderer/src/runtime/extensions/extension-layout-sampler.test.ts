import assert from "node:assert/strict";
import test, { before } from "node:test";

import type { ExtensionLayoutSampler, FrameLayout } from "./extension-layout-sampler";

// 模块加载时调用 setupDomEnvironment，关闭 window 的 after 钩子属于整个文件；在 before 中调用时它会在 before 结束后立即运行。
// setupDomEnvironment is called at module load so the after hook that closes the window belongs to the file; called
// inside before, it would run as soon as before ends.
const environmentReady = import("@agw/test-harness").then(({ setupDomEnvironment }) =>
  setupDomEnvironment(),
);

let Sampler: typeof ExtensionLayoutSampler;

before(async () => {
  await environmentReady;
  ({ ExtensionLayoutSampler: Sampler } = await import("./extension-layout-sampler"));
});

const nextFrame = () => new Promise((resolve) => requestAnimationFrame(() => resolve(null)));

/** jsdom 没有排版：给占位元素一个矩形，测试改变它模拟布局变化。jsdom has no layout: the slot gets a rectangle the test changes. */
function slotElement(rect: { x: number; y: number; width: number; height: number }) {
  const element = document.createElement("div");
  document.body.append(element);
  const current = { ...rect };
  Object.defineProperty(element, "getBoundingClientRect", {
    value: () => ({
      left: current.x,
      top: current.y,
      width: current.width,
      height: current.height,
      right: current.x + current.width,
      bottom: current.y + current.height,
      x: current.x,
      y: current.y,
    }),
  });
  return { element, rect: current };
}

function createSampler() {
  const reports: [string, FrameLayout][] = [];
  const sampler = new Sampler(document, (frameId, layout) => reports.push([frameId, layout]));
  return { sampler, reports };
}

test("a registered slot reports its area once and again only after a scheduled change", async () => {
  const { sampler, reports } = createSampler();
  const { element, rect } = slotElement({ x: 10, y: 20, width: 300, height: 200 });
  const unregister = sampler.register("frame-1", element);
  await nextFrame();
  await nextFrame();
  assert.deepEqual(reports, [
    ["frame-1", { bounds: { x: 10, y: 20, width: 300, height: 200 }, visible: true }],
  ]);

  // 没有变化的测量不发送。A measurement without changes sends nothing.
  sampler.schedule();
  await nextFrame();
  assert.equal(reports.length, 1);

  // 不连续采样时，只有调度后才看到仅位置变化。Without continuous sampling, a position-only change shows up after a schedule.
  rect.x = 40;
  await nextFrame();
  assert.equal(reports.length, 1);
  sampler.schedule();
  await nextFrame();
  assert.deepEqual(reports[1], [
    "frame-1",
    { bounds: { x: 40, y: 20, width: 300, height: 200 }, visible: true },
  ]);

  unregister();
  assert.deepEqual(reports[2], [
    "frame-1",
    { bounds: { x: 0, y: 0, width: 0, height: 0 }, visible: false },
  ]);
  element.remove();
});

test("continuous sampling follows position and CSS visibility changes every frame and stops when turned off", async () => {
  const { sampler, reports } = createSampler();
  const { element, rect } = slotElement({ x: 0, y: 0, width: 100, height: 100 });
  const unregister = sampler.register("frame-1", element);
  sampler.setContinuous(true);
  await nextFrame();
  assert.equal(reports.length, 1);

  rect.y = 30;
  await nextFrame();
  await nextFrame();
  assert.deepEqual(reports.at(-1), [
    "frame-1",
    { bounds: { x: 0, y: 30, width: 100, height: 100 }, visible: true },
  ]);

  // CSS 隐藏的区域继续采样：visibility、透明度与自身的 content-visibility。Slots hidden by CSS keep being sampled:
  // visibility, opacity and the slot's own content-visibility.
  for (const [property, value] of [
    ["visibility", "hidden"],
    ["opacity", "0"],
    ["content-visibility", "hidden"],
    ["display", "none"],
  ] as const) {
    const reported: number = reports.length;
    element.style.setProperty(property, value);
    await nextFrame();
    await nextFrame();
    assert.equal(reports.length, reported + 1, property);
    assert.equal(reports.at(-1)?.[1].visible, false, property);
    element.style.removeProperty(property);
    await nextFrame();
    await nextFrame();
    assert.equal(reports.at(-1)?.[1].visible, true, property);
  }

  sampler.setContinuous(false);
  await nextFrame();
  const count = reports.length;
  rect.y = 60;
  await nextFrame();
  await nextFrame();
  assert.equal(reports.length, count);
  unregister();
  element.remove();
});

test("an ancestor hidden by CSS hides the native view", async () => {
  const { sampler, reports } = createSampler();
  const parent = document.createElement("section");
  document.body.append(parent);
  const { element } = slotElement({ x: 0, y: 0, width: 100, height: 100 });
  parent.append(element);
  const unregister = sampler.register("frame-1", element);
  sampler.setContinuous(true);
  await nextFrame();
  assert.equal(reports.at(-1)?.[1].visible, true);
  parent.style.setProperty("content-visibility", "hidden");
  await nextFrame();
  await nextFrame();
  assert.equal(reports.at(-1)?.[1].visible, false);
  unregister();
  parent.remove();
});
