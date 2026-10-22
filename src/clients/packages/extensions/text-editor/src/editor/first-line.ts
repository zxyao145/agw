// 块的第一行（Markdown 排版设计第 5 节）：拖动手柄垂直居中在这一行上。依赖浏览器计算的布局。
// A block's first line (Markdown typography design, section 5): the drag handle is vertically centered on it. Relies
// on the layout the browser computes.

const CONTROLS = "img, input, select, button";
const SIZED = `${CONTROLS}, hr`;

/** 计算行高，单位 px。The computed line height in px. */
function lineHeightOf(element: Element): number {
  const value = getComputedStyle(element).lineHeight;
  if (!value.endsWith("px")) {
    throw new Error(`Expected a px line height on <${element.localName}>, got "${value}".`);
  }
  return parseFloat(value);
}

/** 宽高都大于 0 的外框，否则为 null。The box when its width and height are above 0, otherwise null. */
function sizedBox(element: Element): DOMRect | null {
  const box = element.getBoundingClientRect();
  return box.width > 0 && box.height > 0 ? box : null;
}

/**
 * 第一个有布局的非空白字符所在的行框：高度不足文字元素的计算行高时按行高居中扩展。
 * The line box of the first laid-out non-space character; a box shorter than the text element's computed line height
 * grows to it around the same center.
 */
function firstTextLine(element: HTMLElement): { node: Text; line: DOMRect } | null {
  const walker = element.ownerDocument.createTreeWalker(element, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
    const text = node as Text;
    const index = text.data.search(/\S/);
    if (index < 0) continue;
    const range = element.ownerDocument.createRange();
    range.setStart(text, index);
    range.setEnd(text, index + 1);
    const box = range.getClientRects()[0];
    // 没有布局（display: none、hidden）时 getClientRects() 为空。Without layout (display: none, hidden) it is empty.
    if (box === undefined) continue;
    const height = Math.max(box.height, lineHeightOf(text.parentElement!));
    return {
      node: text,
      line: new DOMRect(box.left, box.top + (box.height - height) / 2, box.width, height),
    };
  }
  return null;
}

/**
 * 块在页面上显示的第一行（设计第 5 节的三条规则）：
 * 1. 第一个非空白字符的行框；
 * 2. 它之前第一个有尺寸的表单控件或图片，顶部不低于这个行框时，取该控件的外框；
 * 3. 没有文字时取第一个有尺寸的 img、hr、input、select、button 的外框，都没有时取块外框，高度为块的计算行高。
 * The first line a block shows (the three rules of design section 5):
 * 1. the line box of the first non-space character;
 * 2. the box of the first sized form control or image before it, when that box's top is not below the line box;
 * 3. without text, the box of the first sized img, hr, input, select or button, else the block box with the block's
 *    computed line height as its height.
 */
export function firstLineRect(element: HTMLElement): DOMRect {
  const text = firstTextLine(element);
  if (text === null) {
    const candidates = element.matches(SIZED) ? [element] : element.querySelectorAll(SIZED);
    for (const candidate of candidates) {
      const box = sizedBox(candidate);
      if (box !== null) return box;
    }
    const box = element.getBoundingClientRect();
    return new DOMRect(box.left, box.top, box.width, lineHeightOf(element));
  }
  for (const control of element.querySelectorAll(CONTROLS)) {
    // 文档顺序中位于文字之后的控件不再考虑。Controls after the text in document order are not considered.
    if (!(control.compareDocumentPosition(text.node) & Node.DOCUMENT_POSITION_FOLLOWING)) break;
    const box = sizedBox(control);
    if (box === null) continue;
    return box.top < text.line.bottom ? box : text.line;
  }
  return text.line;
}
