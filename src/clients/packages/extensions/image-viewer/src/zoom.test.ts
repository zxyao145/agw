import assert from "node:assert/strict";
import test from "node:test";

import {
  fitScale,
  imagePointUnder,
  layoutImage,
  MAX_SCALE,
  PADDING,
  type Point,
  scrollToPoint,
  type Size,
  wheelScale,
  zoomIn,
  zoomOut,
} from "./zoom";

const STAGE = { width: 800, height: 600 };

/**
 * 从 `from` 缩放到 `to` 后的滚动位置：先取 anchor 下的图片点，再求使它回到 anchor 下的滚动位置，与查看器的用法相同。
 * The scroll position after zooming from `from` to `to`: the image point under the anchor, then the scroll position
 * that brings it back under the anchor, as the viewer does.
 */
function zoomScroll(options: {
  image: Size;
  stage: Size;
  scroll: Point;
  from: number;
  to: number;
  anchor: Point;
}): Point {
  const { image, stage, scroll, from, to, anchor } = options;
  const point = imagePointUnder({ image, stage, scale: from, scroll, anchor });
  return scrollToPoint({ image, stage, scale: to, point, anchor });
}

test("fitScale_SmallImage_KeepsActualSize", () => {
  assert.equal(fitScale({ width: 100, height: 50 }, STAGE), 1);
});

test("fitScale_LargeImage_FitsTheTighterSideInsidePadding", () => {
  // 宽图受宽度限制，高图受高度限制。A wide image is bound by width, a tall one by height.
  assert.equal(fitScale({ width: 3072, height: 1000 }, STAGE), (800 - 2 * PADDING) / 3072);
  assert.equal(fitScale({ width: 1000, height: 2840 }, STAGE), (600 - 2 * PADDING) / 2840);
});

test("fitScale_TinyStage_StaysPositive", () => {
  assert.equal(fitScale({ width: 100, height: 100 }, { width: 10, height: 0 }), 0.01);
});

test("layoutImage_ImageSmallerThanStage_CentersInTheStage", () => {
  assert.deepEqual(layoutImage({ width: 100, height: 50 }, STAGE, 1), {
    content: { width: 800, height: 600 },
    left: 350,
    top: 275,
    width: 100,
    height: 50,
  });
});

test("layoutImage_ImageLargerThanStage_AddsPaddingAroundTheImage", () => {
  assert.deepEqual(layoutImage({ width: 1000, height: 400 }, STAGE, 2), {
    content: { width: 2000 + 2 * PADDING, height: 800 + 2 * PADDING },
    left: PADDING,
    top: PADDING,
    width: 2000,
    height: 800,
  });
});

test("layoutImage_FitScale_ContentEqualsStage", () => {
  const image = { width: 3000, height: 2000 };
  const layout = layoutImage(image, STAGE, fitScale(image, STAGE));
  assert.deepEqual(layout.content, STAGE);
});

test("zoomIn_OnAndBetweenLevels_ReturnsTheNextLevel", () => {
  assert.equal(zoomIn(1), 1.5);
  assert.equal(zoomIn(1 - 1e-9), 1.5);
  assert.equal(zoomIn(1.2), 1.5);
  assert.equal(zoomIn(0.03), 0.05);
});

test("zoomIn_AtMaximum_ReturnsNull", () => {
  assert.equal(zoomIn(MAX_SCALE), null);
});

test("zoomOut_OnAndBetweenLevels_ReturnsThePreviousLevel", () => {
  assert.equal(zoomOut(1), 0.75);
  assert.equal(zoomOut(1 + 1e-9), 0.75);
  assert.equal(zoomOut(1.2), 1);
  assert.equal(zoomOut(25), 20);
});

test("zoomOut_AtOrBelowMinimum_ReturnsNull", () => {
  assert.equal(zoomOut(0.05), null);
  assert.equal(zoomOut(0.03), null);
});

test("wheelScale_PixelDelta_ScrollingUpZoomsIn", () => {
  assert.equal(wheelScale(1, { deltaY: -300, deltaMode: 0 }, 600, 0.5), Math.E);
  assert.equal(wheelScale(1, { deltaY: 300, deltaMode: 0 }, 600, 0.5), 1 / Math.E);
});

test("wheelScale_LineAndPageDeltas_ConvertToPixels", () => {
  assert.equal(
    wheelScale(1, { deltaY: -3, deltaMode: 1 }, 600, 0.5),
    wheelScale(1, { deltaY: -48, deltaMode: 0 }, 600, 0.5),
  );
  assert.equal(
    wheelScale(1, { deltaY: 1, deltaMode: 2 }, 600, 0.5),
    wheelScale(1, { deltaY: 600, deltaMode: 0 }, 600, 0.5),
  );
});

test("wheelScale_Extremes_ClampToTheRange", () => {
  assert.equal(wheelScale(1, { deltaY: -100_000, deltaMode: 0 }, 600, 0.5), MAX_SCALE);
  assert.equal(wheelScale(1, { deltaY: 100_000, deltaMode: 0 }, 600, 0.5), 0.05);
  // 适应比例低于最小档位时可以缩小到适应比例。Below the smallest level the fit scale is still reachable.
  assert.equal(wheelScale(1, { deltaY: 100_000, deltaMode: 0 }, 600, 0.02), 0.02);
});

test("imagePointUnder_CenteredAndScrolledImages_ReturnsImagePixels", () => {
  // 小图居中：显示区中心是图片中心。A centered small image: the display center is the image center.
  assert.deepEqual(
    imagePointUnder({
      image: { width: 100, height: 50 },
      stage: STAGE,
      scale: 1,
      scroll: { x: 0, y: 0 },
      anchor: { x: 400, y: 300 },
    }),
    { x: 50, y: 25 },
  );
  // 放大并滚动后：内容区坐标减去 PADDING 再除以比例。Zoomed and scrolled: content coordinates minus PADDING over scale.
  assert.deepEqual(
    imagePointUnder({
      image: { width: 1000, height: 1000 },
      stage: STAGE,
      scale: 2,
      scroll: { x: 216, y: 116 },
      anchor: { x: 0, y: 100 },
    }),
    { x: 100, y: 100 },
  );
});

test("scrollToPoint_FromFitToActualSizeAtCenter_KeepsTheCenter", () => {
  const image = { width: 1000, height: 1000 };
  const fit = fitScale(image, STAGE);
  const scroll = zoomScroll({
    image,
    stage: STAGE,
    scroll: { x: 0, y: 0 },
    from: fit,
    to: 1,
    anchor: { x: 400, y: 300 },
  });
  const content = layoutImage(image, STAGE, 1).content;
  assert.deepEqual(scroll, {
    x: (content.width - STAGE.width) / 2,
    y: (content.height - STAGE.height) / 2,
  });
});

test("scrollToPoint_InsideTheRange_KeepsThePointUnderTheAnchor", () => {
  const image = { width: 1200, height: 900 };
  const from = 1.5;
  const to = 4;
  const scroll = { x: 300, y: 200 };
  const anchor = { x: 250, y: 120 };
  const next = zoomScroll({ image, stage: STAGE, scroll, from, to, anchor });
  const before = layoutImage(image, STAGE, from);
  const after = layoutImage(image, STAGE, to);
  // 同一个图片点在缩放前后都位于 anchor 下。The same image point lies under the anchor before and after.
  assert.ok(
    Math.abs((scroll.x + anchor.x - before.left) / from - (next.x + anchor.x - after.left) / to) <
      1e-9,
  );
  assert.ok(
    Math.abs((scroll.y + anchor.y - before.top) / from - (next.y + anchor.y - after.top) / to) <
      1e-9,
  );
});

test("scrollToPoint_OutsideTheRange_ClampsToTheScrollableArea", () => {
  const image = { width: 1000, height: 1000 };
  const atStart = zoomScroll({
    image,
    stage: STAGE,
    scroll: { x: 0, y: 0 },
    from: 1,
    to: 2,
    anchor: { x: 0, y: 0 },
  });
  assert.deepEqual(atStart, { x: 0, y: 0 });
  const content = layoutImage(image, STAGE, 2).content;
  const atEnd = zoomScroll({
    image,
    stage: STAGE,
    scroll: { x: 1000 + 2 * PADDING - 800, y: 1000 + 2 * PADDING - 600 },
    from: 1,
    to: 2,
    anchor: { x: 800, y: 600 },
  });
  assert.deepEqual(atEnd, { x: content.width - 800, y: content.height - 600 });
});

test("scrollToPoint_ImageSmallerThanStage_ReturnsZero", () => {
  assert.deepEqual(
    zoomScroll({
      image: { width: 100, height: 50 },
      stage: STAGE,
      scroll: { x: 0, y: 0 },
      from: 1,
      to: 2,
      anchor: { x: 600, y: 100 },
    }),
    { x: 0, y: 0 },
  );
});
