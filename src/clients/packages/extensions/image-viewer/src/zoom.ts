// 显示区中图片的布局与缩放计算，全部是纯函数；组件只处理事件与 DOM。
// Layout and zoom calculations for the image in the display area, all pure; the component handles events and DOM.

export type Size = { width: number; height: number };
export type Point = { x: number; y: number };

/** 显示区四周留出的像素。The pixels kept free around the image in the display area. */
export const PADDING = 16;

/** 缩放按钮依次使用的比例。The scales the zoom buttons step through. */
export const ZOOM_LEVELS = [0.05, 0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16, 20] as const;

export const MAX_SCALE = ZOOM_LEVELS[ZOOM_LEVELS.length - 1];

/** 不小于这个比例时逐个像素显示。From this scale on, pixels are drawn without smoothing. */
export const PIXELATED_SCALE = 3;

/** 比较比例时允许的误差。The tolerance when comparing scales. */
const EPSILON = 1e-6;

/** 一个方向上的布局：内容区长度与图片起点。The layout along one axis: the content length and the image start. */
function layoutAxis(stage: number, image: number, scale: number) {
  const length = image * scale;
  const content = Math.max(stage, length + 2 * PADDING);
  return { length, content, start: (content - length) / 2 };
}

/**
 * 适应显示区的比例：四周留出 PADDING，不超过 1，显示区很小时仍大于 0。
 * The scale that fits the display area: PADDING on every side, at most 1, and above 0 even for a tiny area.
 */
export function fitScale(image: Size, stage: Size): number {
  return Math.min(
    1,
    Math.max(1, stage.width - 2 * PADDING) / image.width,
    Math.max(1, stage.height - 2 * PADDING) / image.height,
  );
}

export type ImageLayout = {
  /** 可以滚动的内容区。The scrollable content. */
  content: Size;
  /** 图片在内容区中的位置与显示尺寸。The image position in the content and its displayed size. */
  left: number;
  top: number;
  width: number;
  height: number;
};

/**
 * 按比例排列图片：内容区至少与显示区一样大，图片在其中居中。
 * Lays out the image at a scale: the content is at least as large as the display area, with the image centered.
 */
export function layoutImage(image: Size, stage: Size, scale: number): ImageLayout {
  const x = layoutAxis(stage.width, image.width, scale);
  const y = layoutAxis(stage.height, image.height, scale);
  return {
    content: { width: x.content, height: y.content },
    left: x.start,
    top: y.start,
    width: x.length,
    height: y.length,
  };
}

/** 大于当前比例的第一个档位；没有时为 null。The first level above the scale, or null. */
export function zoomIn(scale: number): number | null {
  return ZOOM_LEVELS.find((level) => level > scale + EPSILON) ?? null;
}

/** 小于当前比例的最后一个档位；没有时为 null。The last level below the scale, or null. */
export function zoomOut(scale: number): number | null {
  return ZOOM_LEVELS.findLast((level) => level < scale - EPSILON) ?? null;
}

/** 滚轮缩放的下限：最小档位与适应比例中较小的一个。The wheel zoom's lower bound. */
export function minScale(fit: number): number {
  return Math.min(ZOOM_LEVELS[0], fit);
}

/**
 * 带 Ctrl 或 Cmd 的滚轮（含触控板捏合）之后的比例：`deltaMode` 为行时乘以 16、为页时乘以显示区高度，向上滚动放大。
 * The scale after a wheel event with Ctrl or Cmd (pinches included): lines count 16 pixels and pages the display
 * height, and scrolling up zooms in.
 */
export function wheelScale(
  scale: number,
  wheel: { deltaY: number; deltaMode: number },
  stageHeight: number,
  fit: number,
): number {
  const pixels =
    wheel.deltaMode === 1
      ? wheel.deltaY * 16
      : wheel.deltaMode === 2
        ? wheel.deltaY * stageHeight
        : wheel.deltaY;
  return Math.min(MAX_SCALE, Math.max(minScale(fit), scale * Math.exp(-pixels / 300)));
}

/**
 * 滚动位置为 `scroll` 时 `anchor`（显示区中的坐标）下的点，以图片像素为单位，可以在图片之外。
 * The point under `anchor` (display-area coordinates) at scroll position `scroll`, in image pixels; it may lie outside
 * the image.
 */
export function imagePointUnder(options: {
  image: Size;
  stage: Size;
  scale: number;
  scroll: Point;
  anchor: Point;
}): Point {
  const { image, stage, scale, scroll, anchor } = options;
  const layout = layoutImage(image, stage, scale);
  return {
    x: (scroll.x + anchor.x - layout.left) / scale,
    y: (scroll.y + anchor.y - layout.top) / scale,
  };
}

/**
 * 使图片点 `point` 位于 `anchor`（显示区中的坐标）下的滚动位置，限制在可滚动范围内。
 * The scroll position that puts image point `point` under `anchor` (display-area coordinates), clamped to the
 * scrollable range.
 */
export function scrollToPoint(options: {
  image: Size;
  stage: Size;
  scale: number;
  point: Point;
  anchor: Point;
}): Point {
  const { image, stage, scale, point, anchor } = options;
  const layout = layoutImage(image, stage, scale);
  const clamp = (value: number, max: number) => Math.min(Math.max(value, 0), max);
  return {
    x: clamp(layout.left + point.x * scale - anchor.x, layout.content.width - stage.width),
    y: clamp(layout.top + point.y * scale - anchor.y, layout.content.height - stage.height),
  };
}
